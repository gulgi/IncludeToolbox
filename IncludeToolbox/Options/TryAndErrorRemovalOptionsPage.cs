using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace IncludeToolbox
{
    [Guid("DBC8A65D-8B86-4296-9F1F-E785B182B550")]
    public class TryAndErrorRemovalOptionsPage : DialogPage
    {
        public const string SubCategory = "Try and Error Include Removal";
        private const string collectionName = "TryAndErrorRemoval";

        public enum IncludeRemovalOrder
        {
            BottomToTop,
            TopToBottom,
        }
        [Category("Try And Error Include Removal")]
        [DisplayName("Removal Order")]
        [Description("Gives the order which #includes are removed.")]
        public IncludeRemovalOrder RemovalOrder { get; set; } = IncludeRemovalOrder.BottomToTop;

        [Category("Try And Error Include Removal")]
        [DisplayName("Ignore First Include")]
        [Description("If true, the first include of a file will never be removed (useful for ignoring PCH).")]
        public bool IgnoreFirstInclude { get; set; } = true;

        [Category("Try And Error Include Removal")]
        [DisplayName("Ignore List")]
        [Description("List of regexes. If the content of a #include directive match with any of these, it will be ignored.")]
        public string[] IgnoreList { get; set; } = new string[] { ".inl", "_inl.h" };

        [Category("Try And Error Include Removal")]
        [DisplayName("Keep Line Breaks")]
        [Description("If true, removed includes will leave an empty line.")]
        public bool KeepLineBreaks { get; set; } = false;


        // In theory the whole save/load mechanism should be done automatically.
        // But *something* is broken there.
        // see http://stackoverflow.com/questions/32751040/store-array-in-options-using-dialogpage


        private WritableSettingsStore GetSettingsStore()
        {
            var settingsManager = new ShellSettingsManager(ServiceProvider.GlobalProvider);
            return settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);
        }

        public override void SaveSettingsToStorage()
        {
            var settingsStore = GetSettingsStore();

            if (!settingsStore.CollectionExists(collectionName))
                settingsStore.CreateCollection(collectionName);

            settingsStore.SetInt32(collectionName, nameof(RemovalOrder), (int)RemovalOrder);
            settingsStore.SetBoolean(collectionName, nameof(IgnoreFirstInclude), IgnoreFirstInclude);

            var value = string.Join("\n", IgnoreList);
            settingsStore.SetString(collectionName, nameof(IgnoreList), value);

            settingsStore.SetBoolean(collectionName, nameof(KeepLineBreaks), KeepLineBreaks);
        }

        public override void LoadSettingsFromStorage()
        {
            var settingsStore = GetSettingsStore();

            if (settingsStore.PropertyExists(collectionName, nameof(RemovalOrder)))
                RemovalOrder = (IncludeRemovalOrder)settingsStore.GetInt32(collectionName, nameof(RemovalOrder));
            if (settingsStore.PropertyExists(collectionName, nameof(IgnoreFirstInclude)))
                IgnoreFirstInclude = settingsStore.GetBoolean(collectionName, nameof(IgnoreFirstInclude));

            if (settingsStore.PropertyExists(collectionName, nameof(IgnoreList)))
            {
                var value = settingsStore.GetString(collectionName, nameof(IgnoreList));
                IgnoreList = value.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (settingsStore.PropertyExists(collectionName, nameof(KeepLineBreaks)))
                KeepLineBreaks = settingsStore.GetBoolean(collectionName, nameof(KeepLineBreaks));
        }
    }
}
