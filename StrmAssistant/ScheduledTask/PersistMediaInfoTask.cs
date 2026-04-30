using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant
{
    public class PersistMediaInfoTask: IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        public PersistMediaInfoTask(IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _fileSystem = fileSystem;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("MediaInfoPersist - Scheduled Task Execute");
            _logger.Info("Tier2 Max Concurrent Count: " +
                         Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.Tier2MaxConcurrentCount);

            await Task.Yield();
            progress.Report(0);

            var items = Plugin.LibraryApi.FetchPostExtractTaskItems(false);
            _logger.Info("MediaInfoPersist - Number of items: " + items.Count);

            var directoryService = new DirectoryService(_logger, _fileSystem);

            double total = items.Count;
            var index = 0;
            var current = 0;
            var skip = 0;

            var tasks = new List<Task>();

            try
            {
                foreach (var item in items)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        await QueueManager.Tier2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        QueueManager.Tier2Semaphore.Release();
                        break;
                    }

                    var taskIndex = ++index;
                    var taskItem = item;
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                return;
                            }

                            var result = await Plugin.MediaInfoApi.SerializeMediaInfo(taskItem.InternalId, directoryService, false,
                                "Persist MediaInfo Task").ConfigureAwait(false);

                            if (!result) Interlocked.Increment(ref skip);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.Info($"MediaInfoPersist - Item cancelled: {taskItem.Name} - {taskItem.Path}");
                        }
                        catch (Exception e)
                        {
                            _logger.Error($"MediaInfoPersist - Item failed: {taskItem.Name} - {taskItem.Path}");
                            _logger.Error(e.Message);
                            _logger.Debug(e.StackTrace);
                        }
                        finally
                        {
                            QueueManager.Tier2Semaphore.Release();

                            var currentCount = Interlocked.Increment(ref current);
                            progress.Report(currentCount / total * 100);
                        }
                    }, cancellationToken);
                    tasks.Add(task);

                    // 周期性剔除已完成 task，释放它们捕获的 BaseItem 闭包
                    if (tasks.Count >= 100)
                    {
                        tasks.RemoveAll(t => t.IsCompleted);
                    }

                    try
                    {
                        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* expected on cancel */ }
                catch (Exception ex)
                {
                    _logger.Debug("MediaInfoPersist - Drain pending tasks: " + ex.Message);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("MediaInfoPersist - Scheduled Task Cancelled");
                }
                else
                {
                    progress.Report(100.0);
                    _logger.Info($"MediaInfoPersist - Number of items skipped: {skip}");
                    _logger.Info("MediaInfoPersist - Scheduled Task Complete");
                }
            }
            finally
            {
                tasks.Clear();
                items.Clear();
            }
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "MediaInfoPersistTask";

        public string Description => Resources.ResourceManager.GetString(
            "PersistMediaInfoTask_Description_Persists_media_info_to_json_file", Plugin.Instance.DefaultUICulture);

        public string Name => "Persist MediaInfo";
        //public string Name => Resources.ResourceManager.GetString("PersistMediaInfoTask_Name_Persist_MediaInfo",
        //    Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
