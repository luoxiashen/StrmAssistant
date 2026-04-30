using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using System;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class NoBoxsetsAutoCreation: PatchBase<NoBoxsetsAutoCreation>
    {
        private static MethodInfo _ensureLibraryFolder;
        private static MethodInfo _getUserViews;

        public NoBoxsetsAutoCreation()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().UIFunctionOptions.NoBoxsetsAutoCreation)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            if (Plugin.Instance.ApplicationHost.ApplicationVersion >= new Version("4.8.4.0"))
            {
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var collectionManager =
                    embyServerImplementationsAssembly?.GetType(
                        "Emby.Server.Implementations.Collections.CollectionManager");
                _ensureLibraryFolder = collectionManager?.GetMethod("EnsureLibraryFolder",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var userViewManager =
                    embyServerImplementationsAssembly?.GetType("Emby.Server.Implementations.Library.UserViewManager");
                _getUserViews = userViewManager?.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "GetUserViews" &&
                                         (m.GetParameters().Length == 3 || m.GetParameters().Length == 4));
            }
            else
            {
                Plugin.Instance.Logger.Warn("NoBoxsetsAutoCreation - Minimum required server version is 4.8.4.0");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _ensureLibraryFolder, nameof(EnsureLibraryFolderPrefix));
            PatchUnpatch(PatchTracker, apply, _getUserViews, nameof(GetUserViewsPrefix));
        }

        [HarmonyPrefix]
        private static bool EnsureLibraryFolderPrefix()
        {
            return false;
        }

        [HarmonyPrefix]
        private static bool GetUserViewsPrefix(UserViewQuery query, User user, ref Folder[] folders)
        {
            folders = folders.Where(i => !(i is CollectionFolder library) ||
                                         library.CollectionType != CollectionType.BoxSets.ToString())
                .ToArray();

            return true;
        }
    }
}
