using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using EnvDTE;
using IncludeToolbox.IncludeFormatter;
using Microsoft.VisualStudio.Text;

namespace IncludeToolbox
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class TryAndErrorRemoval
    {
        public delegate void FinishedEvent(int numRemovedIncludes, bool canceled);
        public event FinishedEvent OnFileFinished;

        public static bool WorkInProgress { get; private set; }

        private volatile bool lastBuildSuccessful;
        private AutoResetEvent outputWaitEvent = new AutoResetEvent(false);
        private const int timeoutMS = 30000; // 30 seconds


        public void PerformTryAndErrorRemoval(EnvDTE.Document document, TryAndErrorRemovalOptionsPage settings)
        {
            if (document == null)
                return;

            string reasonForFailure;
           
            if (VSUtils.VCUtils.IsCompilableFile(document, out reasonForFailure) == false)
            {
                Output.Instance.WriteLine("Can't compile file: {0}", reasonForFailure);
                return;
            }

            if (WorkInProgress)
            {
                Output.Instance.ErrorMsg("Try and error include removal already in progress!");
                return;
            }
            WorkInProgress = true;

            // Start wait dialog.
            IVsThreadedWaitDialog2 progressDialog = null;
            {
                var dialogFactory = Package.GetGlobalService(typeof(SVsThreadedWaitDialogFactory)) as IVsThreadedWaitDialogFactory;
                if (dialogFactory == null)
                {
                    Output.Instance.WriteLine("Failed to get Dialog Factory for wait dialog.");
                    return;
                }
                dialogFactory.CreateInstance(out progressDialog);
                if (progressDialog == null)
                {
                    Output.Instance.WriteLine("Failed to get create wait dialog.");
                    return;
                }
                string waitMessage = $"Parsing '{document.Name}' ... ";
                progressDialog.StartWaitDialogWithPercentageProgress(
                    szWaitCaption: "Include Toolbox - Running Try & Error Include Removal",
                    szWaitMessage: waitMessage,
                    szProgressText: null,
                    varStatusBmpAnim: null,
                    szStatusBarText: "Running Try & Error Removal - " + waitMessage,
                    fIsCancelable: true,
                    iDelayToShowDialog: 0,
                    iTotalSteps: 20,    // Will be replaced.
                    iCurrentStep: 0);
            }

            // Extract all includes.
            ITextBuffer textBuffer = null;
            Tuple<int, IncludeFormatter.IncludeLineInfo>[] includeLines;
            { 
                // Parsing.
                IncludeFormatter.IncludeLineInfo[] documentLines = null;
                try
                {
                    document.Activate();
                    var documentTextView = VSUtils.GetCurrentTextViewHost();
                    textBuffer = documentTextView.TextView.TextBuffer;
                    string documentText = documentTextView.TextView.TextSnapshot.GetText();
                    documentLines = IncludeFormatter.IncludeLineInfo.ParseIncludes(documentText, false, null);
                }
                catch (Exception ex)
                {
                    Output.Instance.WriteLine("Unexpected error: {0}", ex);
                    progressDialog.EndWaitDialog();
                    return;
                }

                // Filter lines for actual includes.
                var includeLinesEnumberable = documentLines.Select((line, order) => new Tuple<int, IncludeFormatter.IncludeLineInfo>(order, line))
                                                           .Where(x => x.Item2.LineType != IncludeFormatter.IncludeLineInfo.Type.NoInclude);
                // Optionally skip top most include.
                if (settings.IgnoreFirstInclude)
                    includeLinesEnumberable = includeLinesEnumberable.Skip(1);
                // Apply filter ignore regex.
                includeLinesEnumberable = includeLinesEnumberable.Where(line => !settings.IgnoreList.Any(regexPattern => 
                                                          new System.Text.RegularExpressions.Regex(regexPattern).Match(line.Item2.IncludeContent).Success));
                // Reverse order if necessary.
                if (settings.RemovalOrder == TryAndErrorRemovalOptionsPage.IncludeRemovalOrder.BottomToTop)
                    includeLinesEnumberable = includeLinesEnumberable.Reverse();

                includeLines = includeLinesEnumberable.ToArray();
            }
            int numIncludes = includeLines.Count();


            // Hook into build events.
            document.DTE.Events.BuildEvents.OnBuildProjConfigDone += OnBuildConfigFinished;
            document.DTE.Events.BuildEvents.OnBuildDone += OnBuildFinished;


            // The rest runs in a separate thread sicne the compile function is non blocking and we want to use BuildEvents
            // We are not using Task, since we want to make use of WaitHandles - using this together with Task is a bit more complicated to get right.
            outputWaitEvent.Reset();
            new System.Threading.Thread(() =>
            {
                int numRemovedIncludes = 0;
                bool canceled = false;

                try
                {
                    int currentProgressStep = 0;

                    // For ever include line..
                    foreach (Tuple<int, IncludeFormatter.IncludeLineInfo> line in includeLines)
                    {
                        // If we are working from top to bottom, the line number may have changed!
                        int currentLine = line.Item1;
                        if (settings.RemovalOrder == TryAndErrorRemovalOptionsPage.IncludeRemovalOrder.TopToBottom)
                            currentLine -= numRemovedIncludes;

                        // Update progress.
                        string waitMessage = $"Removing #includes from '{document.Name}'";
                        string progressText = $"Trying to remove '{line.Item2.IncludeContent}' ...";
                        progressDialog.UpdateProgress(
                            szUpdatedWaitMessage: waitMessage,
                            szProgressText: progressText,
                            szStatusBarText: "Running Try & Error Removal - " + waitMessage + " - " + progressText,
                            iCurrentStep: currentProgressStep + 1,
                            iTotalSteps: numIncludes + 1,
                            fDisableCancel: false,
                            pfCanceled: out canceled);
                        if (canceled)
                            break;

                        ++currentProgressStep;

                        // Remove include - this needs to be done on the main thread.
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            using (var edit = textBuffer.CreateEdit())
                            {
                                if (settings.KeepLineBreaks)
                                    edit.Delete(edit.Snapshot.Lines.ElementAt(currentLine).Extent);
                                else
                                    edit.Delete(edit.Snapshot.Lines.ElementAt(currentLine).ExtentIncludingLineBreak);
                                edit.Apply();
                            }
                            outputWaitEvent.Set();
                        });
                        outputWaitEvent.WaitOne();

                        // Compile - In rare cases VS tells us that we are still building which should not be possible because we have received OnBuildFinished
                        // As a workaround we just try again a few times.
                        {
                            const int maxNumCompileAttempts = 3;
                            for (int numCompileFails = 0; numCompileFails < maxNumCompileAttempts; ++numCompileFails)
                            {
                                try
                                {
                                    VSUtils.VCUtils.CompileSingleFile(document);
                                }
                                catch (Exception e)
                                {
                                    if (numCompileFails == maxNumCompileAttempts - 1)
                                    {
                                        Output.Instance.WriteLine("Compile Failed:\n{0}", e);

                                        document.Undo();
                                        throw e;
                                    }
                                    else
                                    {
                                        // Try again.
                                        System.Threading.Thread.Sleep(100);
                                        continue;
                                    }
                                }
                                break;
                            }
                        }

                        // Wait till woken.
                        bool noTimeout = outputWaitEvent.WaitOne(timeoutMS);

                        // Undo removal if compilation failed.
                        if (!noTimeout || !lastBuildSuccessful)
                        {
                            Output.Instance.WriteLine("Could not remove #include: '{0}'", line.Item2.IncludeContent);
                            document.Undo();
                            if (!noTimeout)
                            {
                                Output.Instance.ErrorMsg("Compilation of {0} timeouted!", document.Name);
                                break;
                            }
                        }
                        else
                        {
                            Output.Instance.WriteLine("Successfully removed #include: '{0}'", line.Item2.IncludeContent);
                            ++numRemovedIncludes;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Output.Instance.WriteLine("Unexpected error: {0}", ex);
                }
                finally
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // Close Progress bar.
                        progressDialog.EndWaitDialog();

                        // Remove build hook again.
                        document.DTE.Events.BuildEvents.OnBuildDone -= OnBuildFinished;
                        document.DTE.Events.BuildEvents.OnBuildProjConfigDone -= OnBuildConfigFinished;

                        // Message.
                        Output.Instance.WriteLine("Removed {0} #include directives from '{1}'", numRemovedIncludes, document.Name);
                        Output.Instance.OutputToForeground();

                        // Notify that we are done.
                        WorkInProgress = false;
                        OnFileFinished?.Invoke(numRemovedIncludes, canceled);
                    });
                }
            }).Start();
        }

        private void OnBuildFinished(vsBuildScope scope, vsBuildAction action)
        {
            outputWaitEvent.Set();
        }

        private void OnBuildConfigFinished(string project, string projectConfig, string platform, string solutionConfig, bool success)
        {
            lastBuildSuccessful = success;
        }
    }
}