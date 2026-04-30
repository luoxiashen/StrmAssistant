using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Core;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.ScheduledTask
{
    public class RefreshEpisodeTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger _logger = Plugin.Instance.Logger;

        private static readonly Random Random = new Random();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("EpisodeRefresh - Scheduled Task Execute");
            var tier2MaxConcurrentCount = Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions
                .Tier2MaxConcurrentCount;
            _logger.Info("Tier2 Max Concurrent Count: " + tier2MaxConcurrentCount);

            await Task.Yield();
            progress.Report(0);

            var itemsToRefresh = Plugin.LibraryApi.FetchEpisodeRefreshTaskItems();

            IsRunning = true;
            var tasks = new List<Task>();

            try
            {
                double total = itemsToRefresh.Count;
                var index = 0;
                var current = 0;

                foreach (var item in itemsToRefresh)
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
                            await Task.Delay(
                                    Random.Next(0,
                                        Math.Max(0, tier2MaxConcurrentCount - QueueManager.Tier2Semaphore.CurrentCount) *
                                        MetadataApi.RequestIntervalMs), cancellationToken)
                                .ConfigureAwait(false);

                            if (cancellationToken.IsCancellationRequested)
                            {
                                return;
                            }

                            EnableItemExclusiveFeatures(taskItem.InternalId, ExclusiveControl.CatchAllBlock,
                                ExclusiveControl.IgnoreExtSubChange);

                            await Plugin.LibraryApi.RefreshEpisodeMetadata(taskItem, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.Info("EpisodeRefresh - Item cancelled: " + taskItem.Name + " - " + taskItem.Path);
                        }
                        catch (Exception e)
                        {
                            _logger.Info("EpisodeRefresh - Item failed: " + taskItem.Name + " - " + taskItem.Path);
                            _logger.Debug(e.Message);
                            _logger.Debug(e.StackTrace);
                        }
                        finally
                        {
                            QueueManager.Tier2Semaphore.Release();

                            ClearItemExclusiveFeatures(taskItem.InternalId);

                            var currentCount = Interlocked.Increment(ref current);
                            progress.Report(currentCount / total * 100);
                            _logger.Info("EpisodeRefresh - Progress " + currentCount + "/" + total + " - " +
                                         "Task " + taskIndex + ": " + taskItem.Path);
                        }
                    }, cancellationToken);
                    tasks.Add(task);

                    // 周期性剔除已完成 task，释放它们捕获的 Episode 闭包，缩减常驻内存峰值
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

                // 不论成功还是取消，都要 await 已投递的任务收尾。
                // 否则后台 Task.Run 闭包会持续持有 Episode 引用，且 IsRunning 状态错乱。
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* expected on cancel */ }
                catch (Exception ex)
                {
                    _logger.Debug("EpisodeRefresh - Drain pending tasks: " + ex.Message);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("EpisodeRefresh - Scheduled Task Cancelled");
                }
                else
                {
                    progress.Report(100.0);
                    _logger.Info("EpisodeRefresh - Scheduled Task Complete");
                }
            }
            finally
            {
                // 显式清空持有的引用，方便 GC 在下一轮回收
                tasks.Clear();
                itemsToRefresh.Clear();
                IsRunning = false;

                // 任务结束后立即触发一次清理，避免本轮分配的元数据/JSON 对象
                // 在两次定期 tick 之间继续占用 RSS
                MemoryCleaner.RequestCleanup("RefreshEpisodeTask");
            }
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "EpisodeRefreshTask";

        public string Description => Resources.ResourceManager.GetString(
            "EpisodeRefreshTask_Description_Refresh_metadata_for_episodes_missing_overview",
            Plugin.Instance.DefaultUICulture);

        public string Name => "Refresh Episode";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public static bool IsRunning { get; private set; }
    }
}
