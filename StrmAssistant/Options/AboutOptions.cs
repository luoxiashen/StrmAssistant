using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

namespace StrmAssistant.Options
{
    public class AboutOptions : EditableOptionsBase
    {
        public const string UICultureAuto = "auto";

        [DisplayNameL("AboutOptions_EditorTitle_About", typeof(Resources))]
        public override string EditorTitle => Resources.AboutOptions_EditorTitle_About;

        public GenericItemList VersionInfoList { get; set; } = new GenericItemList();

        [Browsable(false)]
        public List<EditorSelectOption> UICultureList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("AboutOptions_DefaultUICulture_Language", typeof(Resources))]
        [DescriptionL("AboutOptions_DefaultUICulture_Description", typeof(Resources))]
        [SelectItemsSource(nameof(UICultureList))]
        public string DefaultUICulture { get; set; } = UICultureAuto;

        [Browsable(false)]
        public bool DebugMode { get; set; } = false;

        [Browsable(false)]
        public string GitHubToken { get; set; } = string.Empty;

        [Browsable(false)]
        public string GitHubProxy { get; set; } = string.Empty;
        
        private static string GetVersionHash()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            var fullVersion = assembly.GetName().Version?.ToString();

            if (informationalVersion != null)
            {
                var parts = informationalVersion.Split('+');
                var shortCommitHash = parts.Length > 1 ? parts[1].Substring(0, 7) : "n/a";
                return $"{fullVersion}+{shortCommitHash}";
            }

            return fullVersion;
        }

        public void Initialize()
        {
            UICultureList.Clear();
            UICultureList.Add(new EditorSelectOption
            {
                Value = UICultureAuto,
                Name = Resources.ResourceManager.GetString("AboutOptions_DefaultUICulture_Auto",
                    Plugin.Instance.DefaultUICulture),
                IsEnabled = true
            });
            UICultureList.Add(new EditorSelectOption { Value = "en-US", Name = "English", IsEnabled = true });
            UICultureList.Add(new EditorSelectOption { Value = "zh-CN", Name = "简体中文", IsEnabled = true });
            UICultureList.Add(new EditorSelectOption { Value = "zh-Hant", Name = "繁體中文", IsEnabled = true });

            VersionInfoList.Clear();

            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = GetVersionHash(),
                    Icon = IconNames.info,
                    IconMode = ItemListIconMode.SmallRegular
                });

            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Repo_Link,
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/sjtuross/StrmAssistant",
                });

            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Wiki_Link,
                    Icon = IconNames.menu_book,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/sjtuross/StrmAssistant/wiki",
                });
        }
    }
}
