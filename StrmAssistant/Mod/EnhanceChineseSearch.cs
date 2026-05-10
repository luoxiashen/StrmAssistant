using HarmonyLib;
using MediaBrowser.Controller.Entities;
using SQLitePCL.pretty;
using StrmAssistant.Common;
using StrmAssistant.Core;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.ModOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Mod
{
    public class EnhanceChineseSearch : PatchBase<EnhanceChineseSearch>
    {
        private static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;
        private static readonly Version Ver4830 = new Version("4.8.3.0");
        private static readonly Version Ver4900 = new Version("4.9.0.0");
        private static readonly Version Ver4930 = new Version("4.9.3.0");
        private static readonly Version Ver4937 = new Version("4.9.0.37");

        private static Type raw;
        private static MethodInfo sqlite3_db_config;
        private static MethodInfo sqlite3_load_extension;
        private static FieldInfo sqlite3_db;
        private static MethodInfo _createConnection;
        private static PropertyInfo _dbFilePath;
        private static MethodInfo _getJoinCommandText;
        private static MethodInfo _createSearchTerm;
        private static MethodInfo _cacheIdsFromTextParams;
        private static bool _getJoinCommandTextReturnsStringBuilder;

        public static string CurrentTokenizerName { get; private set; } = "unknown";

        private static string _tokenizerPath;
        private static readonly object _lock = new object();
        private static bool _patchPhase2Initialized;
        private static bool _simpleQueryAvailable;
        private static bool _tokenizerReady;

        private static bool _excludeOriginalTitle;
        private static bool _traditionalToSimplified;
        private static bool _suppressSearchSuggestions;
        private static bool _digitsAsTmdbId;
        private static bool _extensionNeedsLoading;
        private static CancellationTokenSource _catchUpCts;
        private static bool _useUnicode61Mode;
        private static readonly ConditionalWeakTable<object, object> _loadedConnections =
            new ConditionalWeakTable<object, object>();

        // FTS 增量维护：Emby 自身在新增/更新 MediaItems 时会以原始字段写入 fts_searchN，
        // 不经过 NormalizeWithFullPinyin。我们在 ItemAdded/ItemUpdated 时把 itemId 入队，
        // 在 Emby 提供 library.db 活连接时刷新；搜索路径仍会顺带补刷，作为连接不可用或锁竞争时的兜底。
        private static readonly ConcurrentDictionary<long, byte> _pendingFtsRefresh =
            new ConcurrentDictionary<long, byte>();
        private static readonly ConcurrentStack<long> _pendingFtsRefreshOrder =
            new ConcurrentStack<long>();
        private static volatile string _ftsTableName;
        private static volatile bool _enrichedIndexActive;
        private static volatile object _libraryRepository;
        private static int _ftsRefreshScheduled;
        private static int _restoreFtsScheduled;
        // 单连接串行化，避免与同连接上的其他语句冲突（CacheIdsFromTextParamsPrefix 在搜索路径上调用）。
        private static readonly object _ftsRefreshLock = new object();
        private const int FtsRefreshMaxRowsPerConnection = 50;
        // 自适应调度：空闲时 3s，持续有队列或刷新期间拉长间隔，给 Emby 前台查询让路。
        private const int FtsRefreshIdleDelaySeconds = 3;
        private const int FtsRefreshBusyDelaySeconds = 15;
        private const int FtsRefreshScanDelaySeconds = 60;

        private static readonly Regex DigitsOnlyRegex = new Regex(@"^\d+$", RegexOptions.Compiled);
        private static readonly Regex ChineseCharRegex = new Regex(@"[\u4E00-\u9FFF]", RegexOptions.Compiled);
        private static readonly Dictionary<string, Regex> patterns = new Dictionary<string, Regex>
        {
            { "imdb", new Regex(@"^tt\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tmdb", new Regex(@"^tmdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tvdb", new Regex(@"^tvdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) }
        };

        [DllImport("sqlite3", EntryPoint = "sqlite3_load_extension", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_load_extension_native(IntPtr db, string zFile, string zProc, out IntPtr pzErrMsg);

        [DllImport("sqlite3", EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr p);

        // Emby 自带 SQLite 的库名是 e_sqlite3 (Linux: libe_sqlite3.so / Windows: e_sqlite3.dll)
        [DllImport("e_sqlite3", EntryPoint = "sqlite3_load_extension", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_load_extension_e(IntPtr db, string zFile, string zProc, out IntPtr pzErrMsg);

        [DllImport("e_sqlite3", EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free_e(IntPtr p);

        public EnhanceChineseSearch()
        {
            _tokenizerPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginsPath, "libsimple.so");

            Initialize();

            var modOptions = Plugin.Instance.MainOptionsStore.GetOptions().ModOptions;
            UpdateTuningFlags(modOptions);

            if (modOptions.EnhanceChineseSearch || modOptions.EnhanceChineseSearchRestore)
            {
                if (AppVer >= Ver4830)
                {
                    PatchPhase1();
                }
                else
                {
                    ResetOptions();
                }
            }
        }

        internal static void UpdateTuningFlags(ModOptions modOptions)
        {
            var prefs = modOptions.SearchTuningPreferences ?? string.Empty;
            _excludeOriginalTitle = prefs.IndexOf(SearchTuningOption.ExcludeOriginalTitle.ToString(),
                StringComparison.OrdinalIgnoreCase) >= 0;
            _traditionalToSimplified = prefs.IndexOf(SearchTuningOption.TraditionalToSimplifiedChinese.ToString(),
                StringComparison.OrdinalIgnoreCase) >= 0;
            _suppressSearchSuggestions = prefs.IndexOf(SearchTuningOption.SuppressSearchSuggestions.ToString(),
                StringComparison.OrdinalIgnoreCase) >= 0;
            _digitsAsTmdbId = prefs.IndexOf(SearchTuningOption.DigitsAsTmdbId.ToString(),
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        protected override void OnInitialize()
        {
            try
            {
                // 加载 SQLitePCLRawEx
                var sqlitePCLEx = EmbyVersionAdapter.Instance.TryLoadAssembly("SQLitePCLRawEx.core");
                if (sqlitePCLEx != null)
                {
                    raw = EmbyVersionAdapter.Instance.TryGetType(sqlitePCLEx.GetName().Name, "SQLitePCLEx.raw");
                    if (raw != null)
                    {
                        sqlite3_db_config = raw.GetMethods(BindingFlags.Static | BindingFlags.Public)
                            .FirstOrDefault(m => m.Name == "sqlite3_db_config" && m.GetParameters().Length == 4);
                        sqlite3_load_extension = raw.GetMethods(BindingFlags.Static | BindingFlags.Public)
                            .FirstOrDefault(m => m.Name == "sqlite3_load_extension" && m.GetParameters().Length == 4);
                    }
                }

                // 获取 SQLite 数据库连接字段
                sqlite3_db = typeof(SQLiteDatabaseConnection).GetField("db", BindingFlags.NonPublic | BindingFlags.Instance);

                // 加载 Emby.Sqlite
                var embySqlite = EmbyVersionAdapter.Instance.TryLoadAssembly("Emby.Sqlite");
                if (embySqlite != null)
                {
                    var baseSqliteRepository = EmbyVersionAdapter.Instance.TryGetType(embySqlite.GetName().Name, "Emby.Sqlite.BaseSqliteRepository");
                    if (baseSqliteRepository != null)
                    {
                        // 尝试获取 CreateConnection 方法，处理可能的重载
                        var createConnectionMethods = baseSqliteRepository.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                            .Where(m => m.Name == "CreateConnection")
                            .ToArray();
                        
                        if (createConnectionMethods.Length == 1)
                        {
                            _createConnection = createConnectionMethods[0];
                        }
                        else if (createConnectionMethods.Length > 1)
                        {
                            // 如果有多个重载，选择无参数的版本
                            _createConnection = createConnectionMethods.FirstOrDefault(m => m.GetParameters().Length == 0)
                                ?? createConnectionMethods.FirstOrDefault(m => m.GetParameters().Length == 1);
                            
                            if (Plugin.Instance.DebugMode && _createConnection != null)
                            {
                                Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: Selected CreateConnection with {_createConnection.GetParameters().Length} parameters");
                            }
                        }
                        
                        _dbFilePath = baseSqliteRepository.GetProperty("DbFilePath", 
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                }

                // 加载 Emby.Server.Implementations
                var embyServerImplementationsAssembly = EmbyVersionAdapter.Instance.TryLoadAssembly("Emby.Server.Implementations");
                if (embyServerImplementationsAssembly != null)
                {
                    var sqliteItemRepository = EmbyVersionAdapter.Instance.TryGetType(
                        embyServerImplementationsAssembly.GetName().Name,
                        "Emby.Server.Implementations.Data.SqliteItemRepository");
                    
                    if (sqliteItemRepository != null)
                    {
                        // 处理 GetJoinCommandText 可能的重载
                        var sqliteItemRepositoryType = sqliteItemRepository;
                        _getJoinCommandText = EmbyVersionAdapter.Instance.FindCompatibleMethod(
                            sqliteItemRepositoryType, 
                            "GetJoinCommandText", 
                            BindingFlags.NonPublic | BindingFlags.Instance,
                            // Emby 4.9.1.x 及以上版本，移除了 itemLinks2TableQualifier 参数
                            new[] { typeof(MediaBrowser.Controller.Entities.InternalItemsQuery), typeof(List<KeyValuePair<string, string>>), typeof(string), typeof(bool) },
                            // Emby 4.9.0.x 版本
                            new[] { typeof(MediaBrowser.Controller.Entities.InternalItemsQuery), typeof(List<KeyValuePair<string, string>>), typeof(string), typeof(string), typeof(bool) }
                        );

                        if (_getJoinCommandText != null)
                        {
                            if (Plugin.Instance.DebugMode)
                            {
                                var paramCount = _getJoinCommandText.GetParameters().Length;
                                Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: Selected GetJoinCommandText with {paramCount} parameters");
                            }

                            // 检查返回类型
                            var returnType = _getJoinCommandText.ReturnType;
                            _getJoinCommandTextReturnsStringBuilder = returnType.Name == "StringBuilder" ||
                                returnType.FullName == "System.Text.StringBuilder";
                            
                            if (Plugin.Instance.DebugMode)
                            {
                                Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: GetJoinCommandText returns {returnType.Name}");
                            }
                        }
                        
                        _createSearchTerm = sqliteItemRepository.GetMethod("CreateSearchTerm", 
                            BindingFlags.NonPublic | BindingFlags.Static);
                        _cacheIdsFromTextParams = sqliteItemRepository.GetMethod("CacheIdsFromTextParams",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    }
                }

                // 验证必需的组件
                // 外部 tokenizer 组件仅在 Emby < 4.9.3 时需要（4.9.3+ 内置 simple_query() 支持）
                var missingComponents = new List<string>();
                if (AppVer < Ver4930)
                {
                    if (raw == null) missingComponents.Add("SQLitePCLEx.raw");
                    if (sqlite3_db_config == null) missingComponents.Add("sqlite3_db_config");
                    if (sqlite3_load_extension == null) missingComponents.Add("sqlite3_load_extension");
                    if (sqlite3_db == null) missingComponents.Add("SQLiteDatabaseConnection.db");
                }
                if (_createConnection == null) missingComponents.Add("CreateConnection");
                if (_dbFilePath == null) missingComponents.Add("DbFilePath");
                if (_getJoinCommandText == null) missingComponents.Add("GetJoinCommandText");
                if (_createSearchTerm == null) missingComponents.Add("CreateSearchTerm");
                if (_cacheIdsFromTextParams == null) missingComponents.Add("CacheIdsFromTextParams");

                if (missingComponents.Any())
                {
                    Plugin.Instance.Logger.Warn($"EnhanceChineseSearch: Missing components - {string.Join(", ", missingComponents)}");
                    Plugin.Instance.Logger.Warn($"Chinese search enhancement may not work on Emby {AppVer}");
                    Plugin.Instance.Logger.Info("This feature requires internal SQLite APIs that may have changed in this Emby version");
                    
                    // 标记为不可用
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    
                    EmbyVersionAdapter.Instance.LogCompatibilityInfo(
                        nameof(EnhanceChineseSearch),
                        false,
                        $"{missingComponents.Count} required components not found");
                }
                else
                {
                    Plugin.Instance.Logger.Info("EnhanceChineseSearch: All components loaded successfully");
                    Plugin.Instance.Logger.Info($"Chinese search enhancement is compatible with Emby {AppVer}");
                    
                    EmbyVersionAdapter.Instance.LogCompatibilityInfo(
                        nameof(EnhanceChineseSearch),
                        true,
                        "All required SQLite components available");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error($"EnhanceChineseSearch initialization failed: {ex.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {ex.GetType().Name}");
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
                
                EmbyVersionAdapter.Instance.LogCompatibilityInfo(
                    nameof(EnhanceChineseSearch),
                    false,
                    "Initialization error - feature disabled");
            }
        }

        protected override void Prepare(bool apply)
        {
            // No action needed
        }

        private static void PatchPhase1()
        {
            var isRestoreMode = Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearchRestore;
            Plugin.Instance.Logger.Info($"EnhanceChineseSearch - PatchPhase1 started (restore={isRestoreMode})");

            // 4.9.3+ 内置支持，不需要外部 tokenizer 文件；低版本需要确保 tokenizer 文件存在
            var tokenizerFileOk = AppVer >= Ver4930 || EnsureTokenizerExists();

            if (tokenizerFileOk && PatchUnpatch(Instance.PatchTracker, true, _createConnection,
                    postfix: nameof(CreateConnectionPostfix)))
            {
                Plugin.Instance.Logger.Info("EnhanceChineseSearch - PatchPhase1 succeeded");
                return;
            }

            Plugin.Instance.Logger.Warn("EnhanceChineseSearch - PatchPhase1 Failed");

            if (isRestoreMode)
            {
                // 无法恢复 FTS tokenizer，清除 restore 标记避免反复尝试
                Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Cannot restore FTS, clearing restore flag");
                ResetOptions();
            }
            else
            {
                Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Feature enabled but init failed, check compatibility logs");
                // 保留用户配置，不自动禁用
            }
        }

        private static void PatchPhase2(IDatabaseConnection connection)
        {
            string ftsTableName;

            if (AppVer >= Ver4830)
            {
                ftsTableName = "fts_search9";
            }
            else
            {
                ftsTableName = "fts_search8";
            }

            var tokenizerCheckQuery = $@"
                SELECT 
                    CASE 
                        WHEN instr(lower(sql), 'tokenize=""simple""') > 0
                          OR instr(lower(sql), 'tokenize=''simple''') > 0
                          OR instr(lower(sql), 'tokenize=simple') > 0 THEN 'simple'
                        WHEN instr(lower(sql), 'tokenize=""unicode61 remove_diacritics 2""') > 0
                          OR instr(lower(sql), 'tokenize=''unicode61 remove_diacritics 2''') > 0
                          OR instr(lower(sql), 'tokenize=unicode61 remove_diacritics 2') > 0 THEN 'unicode61 remove_diacritics 2'
                        ELSE 'unknown'
                    END AS tokenizer_name
                FROM 
                    sqlite_master 
                WHERE 
                    type = 'table' AND 
                    name = '{ftsTableName}';";

            var rebuildFtsResult = true;
            var patchSearchFunctionsResult = false;

            try
            {
                using (var statement = connection.PrepareStatement(tokenizerCheckQuery))
                {
                    if (statement.MoveNext())
                    {
                        CurrentTokenizerName = statement.Current?.GetString(0) ?? "unknown";
                    }
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Current tokenizer (before) is " + CurrentTokenizerName);

                var isRestoreMode = Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearchRestore;

                if (isRestoreMode)
                {
                    if (!string.Equals(CurrentTokenizerName, "unicode61 remove_diacritics 2", StringComparison.Ordinal))
                    {
                        rebuildFtsResult = RebuildFts(connection, ftsTableName, "unicode61 remove_diacritics 2");
                    }
                    if (rebuildFtsResult)
                    {
                        CurrentTokenizerName = "unicode61 remove_diacritics 2";
                        StopCatchUpLoop();
                        _enrichedIndexActive = false;
                        Plugin.Instance.Logger.Info("EnhanceChineseSearch - Restore Success");
                        ResetOptions();
                    }
                    else
                    {
                        Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Restore failed, keeping restore flag for next startup");
                    }
                }
                else if (!string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
                {
                    if (Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch)
                    {
                        if (_tokenizerReady)
                        {
                            patchSearchFunctionsResult = PatchSearchFunctions();

                            if (patchSearchFunctionsResult)
                            {
                                var targetTokenizer = _useUnicode61Mode
                                    ? "unicode61 remove_diacritics 2"
                                    : "simple";

                                // 智能启动：tokenizer 已匹配时直接跳过全量重建。
                                // RebuildFts 使用 SQLite 事务保证原子性——tokenizer 已是目标值
                                // 说明上次重建已完整提交，无需再做任何同步检查（避免 O(N) 全表扫描阻塞启动）。
                                // 服务器关闭期间外部工具入库的条目由 ScheduleStartupCatchUp 后台补齐。
                                // 后续新增/更新条目由 ItemAdded 事件 + _pendingFtsRefresh 增量队列覆盖。
                                var tokenizerMatches = string.Equals(CurrentTokenizerName, targetTokenizer, StringComparison.Ordinal);
                                var canSkipRebuild = tokenizerMatches;

                                if (canSkipRebuild)
                                {
                                    Plugin.Instance.Logger.Info(
                                        "EnhanceChineseSearch - Tokenizer matches, skipping FTS rebuild");
                                    rebuildFtsResult = true;
                                }
                                else
                                {
                                    Plugin.Instance.Logger.Info(
                                        "EnhanceChineseSearch - Rebuilding FTS index on startup");
                                    rebuildFtsResult = RebuildFts(connection, ftsTableName, targetTokenizer);
                                }

                                if (rebuildFtsResult)
                                {
                                    CurrentTokenizerName = targetTokenizer;
                                    _ftsTableName = ftsTableName;
                                    _enrichedIndexActive = true;
                                    DrainPendingFtsRefresh(connection);
                                    if (!_pendingFtsRefresh.IsEmpty)
                                        SchedulePendingFtsRefresh();
                                    ScheduleStartupCatchUp();
                                    Plugin.Instance.Logger.Info(_useUnicode61Mode
                                        ? "EnhanceChineseSearch - Load Success (unicode61 enriched mode)"
                                        : "EnhanceChineseSearch - Load Success");
                                }
                            }
                        }
                        else
                        {
                            Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Tokenizer not ready, cannot enable Chinese search");
                            if (string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                            {
                                // FTS 已是 simple 状态但 tokenizer 加载失败，必须恢复为 unicode61 防止搜索崩溃
                                Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Restoring FTS to unicode61 to prevent search failures");
                                rebuildFtsResult = RebuildFts(connection, ftsTableName, "unicode61 remove_diacritics 2");
                                if (rebuildFtsResult)
                                {
                                    CurrentTokenizerName = "unicode61 remove_diacritics 2";
                                }
                                ResetOptions();
                            }
                            else
                            {
                                // FTS 是 unicode61 （安全状态），保留用户配置，记录警告即可
                                Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Tokenizer unavailable, feature inactive but option preserved");
                            }
                            return;
                        }
                    }
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Current tokenizer (after) is " + CurrentTokenizerName);
            }
            catch (Exception e)
            {
                // 记录错误信息，但只在 Debug 模式下记录详细堆栈
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - PatchPhase2 Failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {e.GetType().Name}");
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                    if (e.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                    }
                }
            }

            if (!patchSearchFunctionsResult)
            {
                if (string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                {
                    // Tokenizer 已是 simple，之前已成功启用，保留选项
                    Plugin.Instance.Logger.Info("EnhanceChineseSearch: Patch failed but tokenizer is already 'simple', keeping options enabled");
                }
                else
                {
                    // Patch 失败，FTS 未切换 - 保留选项，用户可查看日志了解原因
                    Plugin.Instance.Logger.Warn("EnhanceChineseSearch: Search function patch failed, Chinese search inactive but option preserved");
                }
            }
            else if (string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
            {
                // FTS 表不存在，状态未知，保留选项待下次启动重试
                Plugin.Instance.Logger.Warn("EnhanceChineseSearch: FTS table not found or tokenizer state unknown, will retry on next start");
            }
        }

        private static bool RebuildFts(IDatabaseConnection connection, string ftsTableName, string tokenizerName)
        {
            connection.BeginTransaction(TransactionMode.Deferred);
            try
            {
                connection.Execute($"DROP TABLE IF EXISTS {ftsTableName}");
                connection.Execute(
                    $"CREATE VIRTUAL TABLE IF NOT EXISTS {ftsTableName} USING FTS5 (Name, OriginalTitle, SeriesName, Album, tokenize=\"{tokenizerName}\", prefix='1 2 3 4')");

                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Filling {ftsTableName} Start");

                var enriched = string.Equals(tokenizerName, "simple", StringComparison.Ordinal) ||
                               (_useUnicode61Mode && tokenizerName.StartsWith("unicode61", StringComparison.Ordinal) &&
                                Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch);

                if (enriched)
                {
                    // 用 C# 循环填充，并将拆字 + 全拼 + 拼音首字母附加到中文字段中以支持中文模糊与拼音搜索
                    PopulateFtsWithPinyin(connection, ftsTableName);
                }
                else
                {
                    string populateQuery;
                    if (AppVer < Ver4900)
                    {
                        populateQuery =
                            $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                            GetSearchColumnNormalization("Name") + ", " +
                            GetSearchColumnNormalization("OriginalTitle") + ", " +
                            GetSearchColumnNormalization("SeriesName") + ", " +
                            GetSearchColumnNormalization("Album") +
                            " from MediaItems";
                    }
                    else
                    {
                        populateQuery =
                            $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                            GetSearchColumnNormalization("Name") + ", " +
                            GetSearchColumnNormalization("OriginalTitle") + ", " +
                            GetSearchColumnNormalization("SeriesName") + ", " +
                            GetSearchColumnNormalization(
                                "(select case when AlbumId is null then null else (select name from MediaItems where Id = AlbumId limit 1) end)") +
                            " from MediaItems";
                    }
                    connection.Execute(populateQuery);
                }

                connection.CommitTransaction();
                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Filling {ftsTableName} Complete");
                return true;
            }
            catch (Exception e)
            {
                connection.RollbackTransaction();

                // 记录错误信息，但只在 Debug 模式下记录详细堆栈
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - RebuildFts Failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {e.GetType().Name}");
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                    if (e.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                    }
                }
            }

            return false;
        }

        // 分块大小：单批最多读取/富化/插入的行数。
        // 富化后的单行字符串可能从几十字节膨胀到几 KB（笛卡尔积 + 后缀），
        // 大库一次性物化全部行会让 LOH 在重建期间长时间占用数 GB。
        // 改为分块流式处理后，峰值内存约等于 chunk * 平均富化大小。
        private const int FtsRebuildChunkSize = 4000;

        private static void PopulateFtsWithPinyin(IDatabaseConnection connection, string ftsTableName)
        {
            // 用 rowid 游标分页：避免 OFFSET 的 O(N²) 扫描，且对单连接读写交替友好。
            var selectChunkQuery = AppVer < Ver4900
                ? "SELECT id, Name, OriginalTitle, SeriesName, Album FROM MediaItems " +
                  "WHERE id > ? ORDER BY id LIMIT ?"
                : "SELECT id, Name, OriginalTitle, SeriesName, " +
                  "(select case when AlbumId is null then null else (select name from MediaItems m2 where m2.Id = AlbumId limit 1) end)" +
                  " FROM MediaItems WHERE id > ? ORDER BY id LIMIT ?";

            var insertQuery = $"INSERT INTO {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) VALUES (?,?,?,?,?)";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalRows = 0;
            long pinyinElapsedMs = 0;
            long insertElapsedMs = 0;

            long lastId = 0;
            // 复用同一份 buffer，避免每个 chunk 都重新分配数组对象。
            var rawBuffer = new (long Id, string Name, string Original, string Series, string Album)[FtsRebuildChunkSize];
            var enrichedBuffer = new (long Id, string Name, string Original, string Series, string Album)[FtsRebuildChunkSize];

            while (true)
            {
                // 1) 读取一个 chunk
                var rawCount = 0;
                sw.Restart();
                using (var selectStmt = connection.PrepareStatement(selectChunkQuery))
                {
                    selectStmt.BindParameters[0].Bind(lastId);
                    selectStmt.BindParameters[1].Bind(FtsRebuildChunkSize);

                    while (selectStmt.MoveNext() && rawCount < FtsRebuildChunkSize)
                    {
                        var row = selectStmt.Current;
                        var id = long.Parse(row.GetString(0));
                        rawBuffer[rawCount++] = (
                            id,
                            row.GetString(1),
                            row.GetString(2),
                            row.GetString(3),
                            row.GetString(4));
                        if (id > lastId) lastId = id;
                    }
                }

                if (rawCount == 0) break;

                // 2) 并行富化（纯函数线程安全）
                var localCount = rawCount;
                System.Threading.Tasks.Parallel.For(0, localCount, i =>
                {
                    var r = rawBuffer[i];
                    enrichedBuffer[i] = (
                        r.Id,
                        NormalizeWithFullPinyin(r.Name) ?? string.Empty,
                        NormalizeWithFullPinyin(r.Original) ?? string.Empty,
                        NormalizeWithFullPinyin(r.Series) ?? string.Empty,
                        NormalizeField(r.Album) ?? string.Empty);
                    // 富化完成立即释放原始字段引用
                    rawBuffer[i] = default;
                });
                pinyinElapsedMs += sw.ElapsedMilliseconds;

                // 3) 串行批量插入，逐元素释放富化字段引用
                sw.Restart();
                using (var insertStmt = connection.PrepareStatement(insertQuery))
                {
                    for (var i = 0; i < localCount; i++)
                    {
                        var r = enrichedBuffer[i];
                        insertStmt.BindParameters[0].Bind(r.Id);
                        insertStmt.BindParameters[1].Bind(r.Name);
                        insertStmt.BindParameters[2].Bind(r.Original);
                        insertStmt.BindParameters[3].Bind(r.Series);
                        insertStmt.BindParameters[4].Bind(r.Album);
                        insertStmt.MoveNext();
                        insertStmt.Reset();
                        enrichedBuffer[i] = default;
                    }
                }
                insertElapsedMs += sw.ElapsedMilliseconds;

                totalRows += localCount;

                // 当前批次的富化字符串多落在 LOH (>=85KB 的合并 StringBuilder 输出常见)。
                // 每 N 个 chunk 触发一次轻量 Gen2 collect，使下一批的分配落到已释放的位置上，
                // 否则 RSS 会在整轮重建期间持续单调上涨。
                if (totalRows % (FtsRebuildChunkSize * 8L) == 0)
                {
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
                }

                if (localCount < FtsRebuildChunkSize) break;
            }

            // 收尾：彻底释放 buffer 并归还 LOH，让之后的搜索/写入路径不被 RSS 拖累。
            rawBuffer = null;
            enrichedBuffer = null;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            // 进一步把页归还操作系统（Windows: WorkingSet, Linux glibc: malloc_trim）
            MemoryCleaner.RequestCleanup("EnhanceChineseSearch FTS Rebuild");

            Plugin.Instance.Logger.Info(
                $"EnhanceChineseSearch - Populated {totalRows} rows in chunks of {FtsRebuildChunkSize} " +
                $"(pinyin {pinyinElapsedMs}ms parallel x{Environment.ProcessorCount}, insert {insertElapsedMs}ms)");
        }

        private static bool IsPinyinFtsIndexReady(IDatabaseConnection connection, string ftsTableName)
        {
            try
            {
                var query = $@"
                    SELECT mi.Name, fts.Name
                    FROM MediaItems mi
                    JOIN {ftsTableName} fts ON fts.rowid = mi.Id
                    WHERE mi.Name IS NOT NULL
                      AND mi.Name GLOB '*[一-龥]*'
                    LIMIT 20";

                using (var statement = connection.PrepareStatement(query))
                {
                    var checkedRows = 0;
                    while (statement.MoveNext())
                    {
                        checkedRows++;
                        var name = statement.Current.GetString(0);
                        var indexedName = statement.Current.GetString(1);
                        var initials = ConvertChineseToPinyinInitials(name);
                        if (!string.IsNullOrEmpty(initials) &&
                            (string.IsNullOrEmpty(indexedName) ||
                             indexedName.IndexOf(initials, StringComparison.OrdinalIgnoreCase) < 0))
                        {
                            Plugin.Instance.Logger.Info(
                                $"EnhanceChineseSearch - Pinyin FTS index missing initials for '{name}', rebuild required");
                            return false;
                        }

                        // 校验后缀 token 是否已写入（用于检测旧格式索引并触发重建）
                        if (!string.IsNullOrEmpty(initials) && initials.Length >= 2 && !string.IsNullOrEmpty(indexedName))
                        {
                            var initialsSuffix = initials.Substring(1);
                            // 后缀作为独立 token 应该被空格隔开
                            var pattern = " " + initialsSuffix;
                            if (indexedName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0 &&
                                !indexedName.StartsWith(initialsSuffix + " ", StringComparison.OrdinalIgnoreCase) &&
                                !indexedName.EndsWith(" " + initialsSuffix, StringComparison.OrdinalIgnoreCase))
                            {
                                Plugin.Instance.Logger.Info(
                                    $"EnhanceChineseSearch - FTS index missing pinyin suffix tokens for '{name}', rebuild required");
                                return false;
                            }
                        }

                        // unicode61 兜底模式还需要校验是否存在拆字（CJK 字符两侧有空格）
                        if (_useUnicode61Mode && !string.IsNullOrEmpty(indexedName))
                        {
                            var hasSpacedCjk = false;
                            for (var i = 0; i < indexedName.Length; i++)
                            {
                                var c = indexedName[i];
                                if (c < 0x4E00 || c > 0x9FFF) continue;
                                var leftOk = i == 0 || indexedName[i - 1] == ' ';
                                var rightOk = i == indexedName.Length - 1 || indexedName[i + 1] == ' ';
                                if (leftOk && rightOk) { hasSpacedCjk = true; break; }
                            }
                            if (!hasSpacedCjk)
                            {
                                Plugin.Instance.Logger.Info(
                                    $"EnhanceChineseSearch - FTS index for '{name}' has no spaced CJK tokens, rebuild required");
                                return false;
                            }
                        }
                    }

                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Pinyin FTS index check failed, rebuild required: {e.Message}");
                return false;
            }
        }

        private static string NormalizeField(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.Replace("'", string.Empty).Replace(".", string.Empty);
        }

        private static string NormalizeWithPinyin(string value)
        {
            var normalized = NormalizeField(value);
            if (string.IsNullOrEmpty(normalized)) return normalized;
            if (!IsChinese(normalized)) return normalized;
            var initials = ConvertChineseToPinyinInitials(normalized);
            return string.IsNullOrEmpty(initials) ? normalized : normalized + " " + initials;
        }

        // 多音字组合枚举上限。命中此上限时降级为：只生成主读音的连写/首字母，
        // 但所有读音的单字 token 仍然写入索引，保证按字搜索（chong / hang / yue 等）有效。
        private const int MaxPolyphonyCombos = 32;

        // 段类型：Chinese=该字所有读音；Literal=连续的小写数字/拉丁字母；Break=分隔（空格/标点）
        private enum SegType { Chinese, Literal, Break }

        private readonly struct PinyinSegment
        {
            public readonly SegType Type;
            public readonly string[] Readings; // 仅 Chinese 用
            public readonly string Literal;    // 仅 Literal 用
            public PinyinSegment(SegType t, string[] r, string lit) { Type = t; Readings = r; Literal = lit; }
        }

        /// <summary>
        /// FTS 索引专用：对中文字段生成富化 token，覆盖以下搜索形态：
        ///   - 原文模糊（蜜 / 语 / 纪）
        ///   - 单字所有读音 token (chong / zhong / hang 等多音字)
        ///   - 全拼连写 / 首字母 (chongqing / cq / zhongqing / zq)
        ///   - 数字+中文混合 (19 层 → 19ceng / 19c；重庆 19 号 → chongqing19hao / cq19h)
        ///   - 上述形式的所有后缀（让 FTS5 前缀匹配模拟"以 ... 结尾"的搜索）
        /// </summary>
        private static string NormalizeWithFullPinyin(string value)
        {
            var normalized = NormalizeField(value);
            if (string.IsNullOrEmpty(normalized)) return normalized;
            if (!ChineseCharRegex.IsMatch(normalized)) return normalized;

            var spaced = SpaceOutCjkChars(normalized);

            // 一次扫描，把字符串切成 segments：汉字 / 字母数字串 / 分隔符。
            var segs = new List<PinyinSegment>(normalized.Length);
            var litBuf = new System.Text.StringBuilder();
            void FlushLiteral()
            {
                if (litBuf.Length == 0) return;
                segs.Add(new PinyinSegment(SegType.Literal, null, litBuf.ToString()));
                litBuf.Clear();
            }

            foreach (var c in normalized)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)
                {
                    var readings = PinyinPolyphony.GetReadings(c);
                    if (readings.Length > 0)
                    {
                        FlushLiteral();
                        for (var i = 0; i < readings.Length; i++)
                            readings[i] = readings[i].ToLowerInvariant();
                        segs.Add(new PinyinSegment(SegType.Chinese, readings, null));
                        continue;
                    }
                    // 没读音的汉字（极冷僻）当 break 处理
                    FlushLiteral();
                    segs.Add(new PinyinSegment(SegType.Break, null, null));
                }
                else if (char.IsLetterOrDigit(c))
                {
                    litBuf.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    FlushLiteral();
                    if (segs.Count > 0 && segs[segs.Count - 1].Type != SegType.Break)
                        segs.Add(new PinyinSegment(SegType.Break, null, null));
                }
            }
            FlushLiteral();

            // 收集汉字 segs 索引（用于笛卡尔积枚举）
            var chineseSegIdx = new List<int>();
            for (var i = 0; i < segs.Count; i++)
                if (segs[i].Type == SegType.Chinese) chineseSegIdx.Add(i);

            if (chineseSegIdx.Count == 0) return spaced;

            var sb = new System.Text.StringBuilder(spaced.Length * 4);
            sb.Append(spaced);

            // 1) 单字所有读音 token：保证按字搜索（chong / hang / yue 等）有效
            foreach (var ci in chineseSegIdx)
            {
                foreach (var r in segs[ci].Readings)
                {
                    sb.Append(' ');
                    sb.Append(r);
                }
            }

            // 2) 笛卡尔积大小判断
            long combos = 1;
            var overflow = false;
            foreach (var ci in chineseSegIdx)
            {
                combos *= segs[ci].Readings.Length;
                if (combos > MaxPolyphonyCombos) { overflow = true; break; }
            }

            var seenFull = new HashSet<string>(StringComparer.Ordinal);
            var seenInitials = new HashSet<string>(StringComparer.Ordinal);

            void Emit(int[] picks)
            {
                // 拼接 mixed_full（拼音空格分隔，原始字母数字直连）
                // 和 mixed_initials（汉字取首字母，原始字母数字直连）
                var fullSb = new System.Text.StringBuilder();
                var initSb = new System.Text.StringBuilder();
                var pi = 0;
                for (var i = 0; i < segs.Count; i++)
                {
                    var s = segs[i];
                    switch (s.Type)
                    {
                        case SegType.Chinese:
                            var r = s.Readings[picks[pi++]];
                            if (fullSb.Length > 0 && fullSb[fullSb.Length - 1] != ' ') fullSb.Append(' ');
                            fullSb.Append(r);
                            fullSb.Append(' ');
                            if (r.Length > 0) initSb.Append(r[0]);
                            break;
                        case SegType.Literal:
                            // 字母数字粘到上一个 token 上，不加空格——这样"19"+"层(ceng)"会形成 "19ceng"
                            // fullSb 末尾此时可能是 ' '（前面是汉字读音），需要去掉以拼接。
                            if (fullSb.Length > 0 && fullSb[fullSb.Length - 1] == ' ')
                                fullSb.Length--;
                            fullSb.Append(s.Literal);
                            initSb.Append(s.Literal);
                            break;
                        case SegType.Break:
                            if (fullSb.Length > 0 && fullSb[fullSb.Length - 1] != ' ') fullSb.Append(' ');
                            if (initSb.Length > 0 && initSb[initSb.Length - 1] != ' ') initSb.Append(' ');
                            break;
                    }
                }

                var fullStr = fullSb.ToString().Trim();
                var initStr = initSb.ToString().Trim();
                if (fullStr.Length == 0 && initStr.Length == 0) return;

                // 全拼连写：去掉所有空格
                var fullConnected = Regex.Replace(fullStr, @"\s+", string.Empty);
                if (fullConnected.Length > 0 && seenFull.Add(fullConnected))
                {
                    // 空格分隔形式（unicode61 拆分单字 token，仍然有用）
                    sb.Append(' ');
                    sb.Append(fullStr);
                    // 连写形式
                    sb.Append(' ');
                    sb.Append(fullConnected);
                    // 后缀
                    AppendSuffixes(sb, fullConnected);
                }

                // 首字母连写
                var initConnected = Regex.Replace(initStr, @"\s+", string.Empty);
                if (initConnected.Length > 0 && seenInitials.Add(initConnected))
                {
                    sb.Append(' ');
                    sb.Append(initConnected);
                    AppendSuffixes(sb, initConnected);
                }
            }

            if (!overflow)
            {
                // 枚举所有汉字读音组合
                var picks = new int[chineseSegIdx.Count];
                while (true)
                {
                    Emit(picks);

                    // 进位
                    var k = picks.Length - 1;
                    while (k >= 0)
                    {
                        picks[k]++;
                        if (picks[k] < segs[chineseSegIdx[k]].Readings.Length) break;
                        picks[k] = 0;
                        k--;
                    }
                    if (k < 0) break;
                }
            }
            else
            {
                // 降级：只用主读音
                Emit(new int[chineseSegIdx.Count]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 把字符串的所有后缀（去掉第一个字符开始）作为额外 token 追加。
        /// 例如 "myj" -> 追加 "yj j"；"miyuji" -> 追加 "iyuji yuji uji ji i"。
        /// 这样 FTS5 前缀匹配可以模拟"中缀"命中（"yj*" 命中 "yj"）。
        /// </summary>
        private static void AppendSuffixes(System.Text.StringBuilder sb, string source)
        {
            if (string.IsNullOrEmpty(source) || source.Length <= 1) return;
            for (var i = 1; i < source.Length; i++)
            {
                sb.Append(' ');
                sb.Append(source, i, source.Length - i);
            }
        }

        /// <summary>
        /// 把中日韩 (CJK 4E00-9FFF) 字符前后插空格，使 unicode61 tokenizer 把每个汉字当独立 token。
        /// 非 CJK 字符原样保留。结果折叠多余空格。
        /// </summary>
        private static string SpaceOutCjkChars(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var sb = new System.Text.StringBuilder(value.Length * 2);
            foreach (var c in value)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                    sb.Append(c);
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(c);
                }
            }
            // 折叠连续空格
            return Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
        }

        private static string ConvertChineseToPinyinInitials(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var matches = ChineseCharRegex.Matches(value);
            if (matches.Count == 0) return string.Empty;

            var chars = new char[matches.Count];
            for (var i = 0; i < matches.Count; i++)
            {
                chars[i] = matches[i].Value[0];
            }

            return ConvertToPinyinInitials(new string(chars))?.ToLowerInvariant();
        }

        private static string GetSearchColumnNormalization(string columnName)
        {
            return "replace(replace(" + columnName + ",'''',' '),'.','')";
        }

        private static bool EnsureTokenizerExists()
        {
            var resourceName = GetTokenizerResourceName();
            var expectedSha1 = GetExpectedSha1();

            if (resourceName == null || expectedSha1 == null) return false;

            try
            {
                if (File.Exists(_tokenizerPath))
                {
                    var existingSha1 = ComputeSha1(_tokenizerPath);

                    if (expectedSha1.ContainsValue(existingSha1))
                    {
                        var highestVersion = expectedSha1.Keys.Max();
                        var highestSha1 = expectedSha1[highestVersion];

                        if (existingSha1 == highestSha1)
                        {
                            Plugin.Instance.Logger.Info(
                                $"EnhanceChineseSearch - Tokenizer exists with matching SHA-1 for the highest version {highestVersion}");
                        }
                        else
                        {
                            var currentVersion = expectedSha1.FirstOrDefault(x => x.Value == existingSha1).Key;
                            Plugin.Instance.Logger.Info(
                                $"EnhanceChineseSearch - Tokenizer exists for version {currentVersion} but does not match the highest version {highestVersion}. Upgrading...");
                            ExportTokenizer(resourceName);
                        }

                        return true;
                    }

                    Plugin.Instance.Logger.Info(
                        "EnhanceChineseSearch - Tokenizer exists but SHA-1 is not recognized. No action taken.");

                    return true;
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Tokenizer does not exist. Exporting...");
                ExportTokenizer(resourceName);

                return true;
            }
            catch (Exception e)
            {
                // 记录错误信息，但只在 Debug 模式下记录详细堆栈
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - EnsureTokenizerExists Failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {e.GetType().Name}");
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                    if (e.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                    }
                }
            }

            return false;
        }

        private static void ExportTokenizer(string resourceName)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                using (var fileStream = new FileStream(_tokenizerPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fileStream);
                }
            }

            Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Exported {resourceName} to {_tokenizerPath}");
        }

        private static string GetTokenizerResourceName()
        {
            var tokenizerNamespace = Assembly.GetExecutingAssembly().GetName().Name + ".Tokenizer";
            var winSimpleTokenizer = $"{tokenizerNamespace}.win.libsimple.so";
            var linuxSimpleTokenizer = $"{tokenizerNamespace}.linux.libsimple.so";

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT when Environment.Is64BitOperatingSystem:
                    return winSimpleTokenizer;
                case PlatformID.Unix when Environment.Is64BitOperatingSystem:
                    return linuxSimpleTokenizer;
                default:
                    return null;
            }
        }

        private static Dictionary<Version, string> GetExpectedSha1()
        {
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 4, 0), "a83d90af9fb88e75a1ddf2436c8b67954c761c83" },
                        { new Version(0, 5, 0), "aed57350b46b51bb7d04321b7fe8e5e60b0cdbdc" }
                    };
                case PlatformID.Unix:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 4, 0), "f7fb8ba0b98e358dfaa87570dc3426ee7f00e1b6" },
                        { new Version(0, 5, 0), "8e36162f96c67d77c44b36093f31ae4d297b15c0" }
                    };
                default:
                    return null;
            }
        }

        private static string ComputeSha1(string filePath)
        {
            using (var sha1 = SHA1.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha1.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void ResetOptions()
        {
            var modOptions = Plugin.Instance.MainOptionsStore.GetOptions().ModOptions;
            modOptions.EnhanceChineseSearch = false;
            modOptions.EnhanceChineseSearchRestore = false;
            Plugin.Instance.MainOptionsStore.SavePluginOptionsSuppress();
        }

        private static bool PatchSearchFunctions()
        {
            // 根据返回类型选择正确的 Postfix 方法
            string getJoinCommandTextPostfix = _getJoinCommandTextReturnsStringBuilder
                ? nameof(GetJoinCommandTextPostfixStringBuilder)
                : nameof(GetJoinCommandTextPostfix);

            bool getJoinResult = PatchUnpatch(Instance.PatchTracker, true, _getJoinCommandText,
                       postfix: getJoinCommandTextPostfix);
            bool createSearchTermResult = PatchUnpatch(Instance.PatchTracker, true, _createSearchTerm,
                       prefix: nameof(CreateSearchTermPrefix));
            bool cacheIdsResult = PatchUnpatch(Instance.PatchTracker, true,
                       _cacheIdsFromTextParams, prefix: nameof(CacheIdsFromTextParamsPrefix));

            Plugin.Instance.Logger.Info($"PatchSearchFunctions: getJoinCommandText={getJoinResult}, createSearchTerm={createSearchTermResult}, cacheIdsFromTextParams={cacheIdsResult}");

            return getJoinResult && createSearchTermResult && cacheIdsResult;
        }

        /// <summary>
        /// 检查 simple_query() 函数是否可用（Emby 4.9.3+ 内置支持）
        /// </summary>
        private static bool IsSimpleQueryFunctionAvailable(IDatabaseConnection connection)
        {
            try
            {
                // 尝试使用 simple_query() 函数进行测试查询
                // 如果可用，这个查询应该不会报错
                var testQuery = "SELECT simple_query('test')";
                using (var statement = connection.PrepareStatement(testQuery))
                {
                    // 如果能执行到这一步，说明 simple_query 函数可用
                }
                Plugin.Instance.Logger.Info("IsSimpleQueryFunctionAvailable: simple_query() is AVAILABLE");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Info($"IsSimpleQueryFunctionAvailable: simple_query() not available: {ex.Message}");
                // simple_query 函数不可用
                return false;
            }
        }

        /// <summary>
        /// 检查 simple tokenizer 是否可用
        /// </summary>
        private static bool IsSimpleTokenizerAvailable(IDatabaseConnection connection)
        {
            try
            {
                // 尝试创建使用 simple tokenizer 的 FTS 表
                connection.Execute("CREATE VIRTUAL TABLE IF NOT EXISTS __test_fts_simple__ USING FTS5(tokenize=\"simple\")");
                connection.Execute("DROP TABLE IF EXISTS __test_fts_simple__");
                return true;
            }
            catch
            {
                // simple tokenizer 不可用
                return false;
            }
        }

        /// <summary>
        /// 检查 unicode61 tokenizer 是否可用（内置，始终可用）
        /// </summary>
        private static bool IsUnicode61TokenizerAvailable(IDatabaseConnection connection)
        {
            try
            {
                // unicode61 是 SQLite FTS5 内置的，始终可用
                connection.Execute("CREATE VIRTUAL TABLE IF NOT EXISTS __test_fts_unicode__ USING FTS5(tokenize=\"unicode61\")");
                connection.Execute("DROP TABLE IF EXISTS __test_fts_unicode__");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool LoadTokenizerExtension(IDatabaseConnection connection)
        {
            // 1. 检查内置 simple_query() 支持（Emby 4.9.3+）
            if (AppVer >= Ver4930)
            {
                if (IsSimpleQueryFunctionAvailable(connection))
                {
                    Plugin.Instance.Logger.Info("EnhanceChineseSearch - Built-in simple tokenizer with simple_query() support");
                    _simpleQueryAvailable = true;
                    _tokenizerReady = true;
                    return true;
                }
                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Emby built-in simple_query() not available, trying external tokenizer");
            }

            // 2. 检查是否原生支持 simple tokenizer（没有 simple_query）
            if (IsSimpleTokenizerAvailable(connection))
            {
                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Simple tokenizer available natively (basic mode, no simple_query)");
                _simpleQueryAvailable = false;
                _tokenizerReady = true;
                return true;
            }

            // 3. 尝试加载外部 libsimple.so（不一定能成功，失败不致命，会回退到 unicode61 模式）
            try
            {
                if (sqlite3_db == null || sqlite3_db_config == null || sqlite3_load_extension == null || raw == null)
                {
                    Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Required SQLite components not found, cannot load tokenizer");
                    return false;
                }

                if (string.IsNullOrEmpty(_tokenizerPath) || !File.Exists(_tokenizerPath))
                {
                    Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - Tokenizer file not found at {_tokenizerPath}");
                    return false;
                }

                var db = sqlite3_db.GetValue(connection);
                if (db == null)
                {
                    Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Could not get SQLite database handle");
                    return false;
                }

                // 使用 SQLITE_DBCONFIG_ENABLE_LOAD_EXTENSION (1005) 启用 C 级扩展加载（不启用 SQL load_extension()）
                // 这是 Pro 代码使用的方式：raw.sqlite3_db_config(db, 1005, 1, ref pOk)
                var pOk = 0;
                var dbConfigArgs = new object[] { db, 1005, 1, pOk };
                sqlite3_db_config.Invoke(null, dbConfigArgs);
                try
                {
                    LoadTokenizerExtensionNative(db);
                }
                finally
                {
                    // 恢复：禁用扩展加载
                    var restoreArgs = new object[] { db, 1005, 0, pOk };
                    try { sqlite3_db_config.Invoke(null, restoreArgs); } catch { }
                }

                // Validate the simple tokenizer is actually available
                connection.Execute("CREATE VIRTUAL TABLE IF NOT EXISTS __test_fts_simple__ USING FTS5(tokenize=\"simple\")");
                connection.Execute("DROP TABLE IF EXISTS __test_fts_simple__");

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - External simple tokenizer loaded and validated");
                _extensionNeedsLoading = true;
                _loadedConnections.GetOrCreateValue(connection);
                _tokenizerReady = true;
                _simpleQueryAvailable = IsSimpleQueryFunctionAvailable(connection);

                return true;
            }
            catch (Exception e)
            {
                var inner = e.InnerException;
                var msg = inner != null ? $"{e.Message} -> {inner.Message}" : e.Message;
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - Load tokenizer failed: {msg}");
            }

            // 4. 兜底：unicode61 富索引模式（不需要 native 扩展，所有平台都能用）
            Plugin.Instance.Logger.Info(
                "EnhanceChineseSearch - Falling back to unicode61 enriched-data mode (no native extension required)");
            _useUnicode61Mode = true;
            _simpleQueryAvailable = false;
            _tokenizerReady = true;
            return true;
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfix(object __instance, ref IDatabaseConnection __result)
        {
            try
            {
                var dbPath = _dbFilePath?.GetValue(__instance) as string;
                if (dbPath == null || !dbPath.EndsWith("library.db", StringComparison.OrdinalIgnoreCase))
                    return;

                _libraryRepository = __instance;

                if (!_patchPhase2Initialized)
                {
                    lock (_lock)
                    {
                        if (!_patchPhase2Initialized)
                        {
                            LoadTokenizerExtension(__result);
                            _patchPhase2Initialized = true;
                            PatchPhase2(__result);
                            return;
                        }
                    }

                    EnsureExtensionLoadedOnConnection(__result);
                }
                else if (_extensionNeedsLoading)
                {
                    EnsureExtensionLoadedOnConnection(__result);
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch: CreateConnectionPostfix error: {ex.Message}");
            }
        }

        private static void EnsureExtensionLoadedOnConnection(IDatabaseConnection connection)
        {
            if (!_extensionNeedsLoading || connection == null) return;
            if (sqlite3_db == null || sqlite3_db_config == null || sqlite3_load_extension == null) return;
            if (_loadedConnections.TryGetValue(connection, out _)) return;

            try
            {
                var db = sqlite3_db?.GetValue(connection);
                if (db == null) return;

                var pOk = 0;
                var dbConfigArgs = new object[] { db, 1005, 1, pOk };
                sqlite3_db_config.Invoke(null, dbConfigArgs);
                try
                {
                    LoadTokenizerExtensionNative(db);
                }
                finally
                {
                    var restoreArgs = new object[] { db, 1005, 0, pOk };
                    try { sqlite3_db_config.Invoke(null, restoreArgs); } catch { }
                }

                _loadedConnections.GetOrCreateValue(connection);
                if (Plugin.Instance.DebugMode)
                    Plugin.Instance.Logger.Debug("EnhanceChineseSearch - Extension loaded on new connection");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - EnsureExtensionLoadedOnConnection failed: {ex.Message}");
            }
        }

        private delegate int Sqlite3LoadExtensionDelegate(IntPtr db, string zFile, string zProc, out IntPtr pzErrMsg);
        private delegate void Sqlite3FreeDelegate(IntPtr p);

        private static Sqlite3LoadExtensionDelegate _resolvedLoadExtension;
        private static Sqlite3FreeDelegate _resolvedFree;
        private static bool _loadExtensionResolved;
        private static string _loadExtensionSource;

        private static void EnsureLoadExtensionResolved()
        {
            if (_loadExtensionResolved) return;
            _loadExtensionResolved = true;

            // 1) 枚举进程已加载模块，匹配 e_sqlite3 / sqlite3 的真实文件路径
            var candidatePaths = new List<string>();
            try
            {
                foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                {
                    var name = m.ModuleName?.ToLowerInvariant() ?? string.Empty;
                    if (name.Contains("e_sqlite3") || name.Contains("sqlite3"))
                    {
                        candidatePaths.Add(m.FileName);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - Enumerate process modules failed: {ex.Message}");
            }

            // 2) 兜底：常见的 native lib 名称
            candidatePaths.AddRange(new[]
            {
                "e_sqlite3", "libe_sqlite3.so", "libe_sqlite3.so.0", "libe_sqlite3.dylib", "e_sqlite3.dll",
                "sqlite3", "libsqlite3.so", "libsqlite3.so.0", "libsqlite3.dylib", "sqlite3.dll"
            });

            foreach (var pathOrName in candidatePaths.Distinct())
            {
                if (string.IsNullOrEmpty(pathOrName)) continue;
                try
                {
                    if (!NativeLibrary.TryLoad(pathOrName, out var libHandle)) continue;
                    if (!NativeLibrary.TryGetExport(libHandle, "sqlite3_load_extension", out var loadFp)) continue;

                    _resolvedLoadExtension = Marshal.GetDelegateForFunctionPointer<Sqlite3LoadExtensionDelegate>(loadFp);
                    if (NativeLibrary.TryGetExport(libHandle, "sqlite3_free", out var freeFp))
                        _resolvedFree = Marshal.GetDelegateForFunctionPointer<Sqlite3FreeDelegate>(freeFp);

                    _loadExtensionSource = pathOrName;
                    Plugin.Instance.Logger.Info(
                        $"EnhanceChineseSearch - Resolved sqlite3_load_extension from '{pathOrName}'");
                    return;
                }
                catch (Exception ex)
                {
                    if (Plugin.Instance.DebugMode)
                        Plugin.Instance.Logger.Debug(
                            $"EnhanceChineseSearch - Try load '{pathOrName}' failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Plugin.Instance.Logger.Warn(
                "EnhanceChineseSearch - Could not resolve sqlite3_load_extension from any candidate library");
        }

        private static void LoadTokenizerExtensionNative(object db)
        {
            var handle = ExtractSqliteHandle(db);
            if (handle == IntPtr.Zero)
            {
                var type = db?.GetType();
                var fields = type == null
                    ? string.Empty
                    : string.Join(", ", type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Select(f => $"{f.Name}:{f.FieldType.FullName}"));
                throw new InvalidOperationException(
                    $"Unable to extract native SQLite handle from {type?.FullName}. Fields=[{fields}]");
            }

            EnsureLoadExtensionResolved();

            // 1) 优先：从 Emby 加载的真实 SQLite 库取函数指针
            if (_resolvedLoadExtension != null)
            {
                try
                {
                    var rcRes = _resolvedLoadExtension(handle, _tokenizerPath, "sqlite3_extension_init",
                        out var errPtrRes);
                    var errRes = errPtrRes != IntPtr.Zero ? Marshal.PtrToStringAnsi(errPtrRes) : null;
                    if (rcRes == 0)
                    {
                        if (Plugin.Instance.DebugMode)
                            Plugin.Instance.Logger.Debug(
                                $"EnhanceChineseSearch - Tokenizer loaded via resolved {_loadExtensionSource}");
                        if (errPtrRes != IntPtr.Zero) _resolvedFree?.Invoke(errPtrRes);
                        return;
                    }

                    Plugin.Instance.Logger.Warn(
                        $"EnhanceChineseSearch - Resolved load_extension ({_loadExtensionSource}) rc={rcRes}, err={errRes ?? "(null)"}, falling back to DllImport");
                    if (errPtrRes != IntPtr.Zero) _resolvedFree?.Invoke(errPtrRes);
                }
                catch (Exception ex)
                {
                    Plugin.Instance.Logger.Warn(
                        $"EnhanceChineseSearch - Resolved load_extension threw {ex.GetType().Name}: {ex.Message}, falling back to DllImport");
                }
            }

            // 2) DllImport e_sqlite3
            try
            {
                var rcE = sqlite3_load_extension_e(handle, _tokenizerPath, "sqlite3_extension_init", out var errPtrE);
                var errE = errPtrE != IntPtr.Zero ? Marshal.PtrToStringAnsi(errPtrE) : null;
                if (rcE == 0)
                {
                    if (Plugin.Instance.DebugMode)
                        Plugin.Instance.Logger.Debug("EnhanceChineseSearch - Tokenizer loaded via DllImport e_sqlite3");
                    if (errPtrE != IntPtr.Zero) sqlite3_free_e(errPtrE);
                    return;
                }
                Plugin.Instance.Logger.Warn(
                    $"EnhanceChineseSearch - DllImport e_sqlite3 rc={rcE}, err={errE ?? "(null)"}, trying system sqlite3");
                if (errPtrE != IntPtr.Zero) sqlite3_free_e(errPtrE);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn(
                    $"EnhanceChineseSearch - DllImport e_sqlite3 threw {ex.GetType().Name}: {ex.Message}");
            }

            // 3) DllImport 系统 sqlite3
            var rc = sqlite3_load_extension_native(handle, _tokenizerPath, "sqlite3_extension_init", out var errorPtr);
            var error = errorPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(errorPtr) : null;

            if (rc == 0) return;

            try
            {
                throw new Exception(string.IsNullOrEmpty(error) ? rc.ToString() : error);
            }
            finally
            {
                if (errorPtr != IntPtr.Zero) sqlite3_free(errorPtr);
            }
        }

        private static IntPtr ExtractSqliteHandle(object db)
        {
            if (db == null) return IntPtr.Zero;
            if (db is IntPtr intPtr) return intPtr;

            var type = db.GetType();
            var dangerousGetHandle = type.GetMethod("DangerousGetHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (dangerousGetHandle != null && dangerousGetHandle.ReturnType == typeof(IntPtr))
            {
                return (IntPtr)dangerousGetHandle.Invoke(db, null);
            }

            foreach (var memberName in new[] { "handle", "_handle", "ptr", "_ptr", "db" })
            {
                var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null) continue;

                var value = field.GetValue(db);
                if (value is IntPtr fieldPtr) return fieldPtr;
                if (value != null && value != db)
                {
                    var nested = ExtractSqliteHandle(value);
                    if (nested != IntPtr.Zero) return nested;
                }
            }

            return IntPtr.Zero;
        }

        private static object CreateSqliteUtf8Parameter(Type parameterType, string value)
        {
            if (parameterType == typeof(string)) return value;

            var nonByRefType = parameterType.IsByRef ? parameterType.GetElementType() : parameterType;
            if (nonByRefType == typeof(string)) return value;
            if (nonByRefType == null) return null;

            var fromString = nonByRefType.GetMethod("FromString", BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            if (fromString != null)
            {
                return fromString.Invoke(null, new object[] { value });
            }

            if (value != null)
            {
                var constructor = nonByRefType.GetConstructor(new[] { typeof(string) });
                if (constructor != null) return constructor.Invoke(new object[] { value });
            }

            if (nonByRefType.IsByRefLike)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - Cannot create SQLite UTF8 parameter for ByRef-like type {nonByRefType.FullName}");
                return null;
            }

            return nonByRefType.IsValueType ? Activator.CreateInstance(nonByRefType) : null;
        }

        private static string ConvertSqliteUtf8ToString(object value)
        {
            if (value == null) return null;
            if (value is string text) return text;

            var type = value.GetType();
            var utf8ToString = type.GetMethod("utf8_to_string", BindingFlags.Instance | BindingFlags.Public);
            if (utf8ToString != null) return utf8ToString.Invoke(value, null) as string;

            return value.ToString();
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfix(InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams, string mediaItemsTableQualifier,
            bool allowJoinOnItemLinks, ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;

            if (!string.IsNullOrEmpty(query.SearchTerm) &&
                __result.IndexOf("match @SearchTerm", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var useNativePrefix = IsPlainAsciiTerm(query.SearchTerm);
                var newMatch = _simpleQueryAvailable && !useNativePrefix
                    ? (_excludeOriginalTitle
                        ? "match '-OriginalTitle:' || simple_query(@SearchTerm)"
                        : "match simple_query(@SearchTerm)")
                    : null;
                if (newMatch != null)
                    __result = ReplaceMatchClause(__result, newMatch);
            }

            if (!string.IsNullOrEmpty(query.Name) &&
                __result.IndexOf("match @SearchTerm", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var namePrefix = _simpleQueryAvailable && !IsPlainAsciiTerm(query.Name)
                    ? "'Name:' || simple_query(@SearchTerm)"
                    : "'Name:' || @SearchTerm";
                __result = ReplaceMatchClause(__result, "match " + namePrefix);

                for (var i = 0; i < bindParams.Count; i++)
                {
                    var kvp = bindParams[i];
                    if (kvp.Key == "@SearchTerm")
                    {
                        var currentValue = kvp.Value;
                        if (currentValue.StartsWith("Name:", StringComparison.Ordinal))
                        {
                            currentValue = currentValue
                                .Substring(currentValue.IndexOf(":", StringComparison.Ordinal) + 1)
                                .Trim('\"', '^', '$')
                                .Replace(".", string.Empty)
                                .Replace("'", string.Empty);
                        }
                        bindParams[i] = new KeyValuePair<string, string>(kvp.Key, currentValue);
                    }
                }
            }
        }

        private static string ReplaceMatchClause(string sql, string replacement)
        {
            var idx = sql.IndexOf("match @SearchTerm", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return sql;
            return sql.Remove(idx, "match @SearchTerm".Length).Insert(idx, replacement);
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfixStringBuilder(InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams, string mediaItemsTableQualifier,
            bool allowJoinOnItemLinks, System.Text.StringBuilder __result)
        {
            if (__result == null) return;

            var resultString = __result.ToString();
            var modified = false;

            if (!string.IsNullOrEmpty(query.SearchTerm) &&
                resultString.IndexOf("match @SearchTerm", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (_simpleQueryAvailable && !IsPlainAsciiTerm(query.SearchTerm))
                {
                    var newMatch = _excludeOriginalTitle
                        ? "match '-OriginalTitle:' || simple_query(@SearchTerm)"
                        : "match simple_query(@SearchTerm)";
                    resultString = ReplaceMatchClause(resultString, newMatch);
                    modified = true;
                }
            }

            if (!string.IsNullOrEmpty(query.Name) &&
                resultString.IndexOf("match @SearchTerm", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var namePrefix = _simpleQueryAvailable && !IsPlainAsciiTerm(query.Name)
                    ? "'Name:' || simple_query(@SearchTerm)"
                    : "'Name:' || @SearchTerm";
                resultString = ReplaceMatchClause(resultString, "match " + namePrefix);
                modified = true;

                for (var i = 0; i < bindParams.Count; i++)
                {
                    var kvp = bindParams[i];
                    if (kvp.Key == "@SearchTerm")
                    {
                        var currentValue = kvp.Value;
                        if (currentValue.StartsWith("Name:", StringComparison.Ordinal))
                        {
                            currentValue = currentValue
                                .Substring(currentValue.IndexOf(":", StringComparison.Ordinal) + 1)
                                .Trim('\"', '^', '$')
                                .Replace(".", string.Empty)
                                .Replace("'", string.Empty);
                        }
                        bindParams[i] = new KeyValuePair<string, string>(kvp.Key, currentValue);
                    }
                }
            }

            if (modified)
            {
                __result.Clear();
                __result.Append(resultString);
            }
        }

        [HarmonyPrefix]
        private static bool CreateSearchTermPrefix(string searchTerm, ref string __result)
        {
            if (string.IsNullOrEmpty(searchTerm))
            {
                __result = searchTerm;
                return false;
            }

            var term = searchTerm.Replace(".", string.Empty).Replace("'", string.Empty);

            if (_traditionalToSimplified && IsChinese(term))
            {
                term = ConvertTraditionalToSimplified(term);
            }

            if (_useUnicode61Mode)
            {
                term = TransformSearchTermForUnicode61(term);
            }
            else if (IsPlainAsciiTerm(term))
            {
                term = term.Trim().ToLowerInvariant();
                if (!term.EndsWith("*", StringComparison.Ordinal)) term += "*";
            }

            __result = term;
            return false;
        }

        private static bool IsPlainAsciiTerm(string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return false;
            var trimmed = term.Trim().Trim('*');
            if (trimmed.Length == 0) return false;
            return trimmed.All(c => c <= 127 && (char.IsLetterOrDigit(c) || c == '_' || c == '-'));
        }

        /// <summary>
        /// unicode61 富索引模式下的查询词改写：
        ///   - 含中文：把中文字符按空格拆开（与索引侧一致）；FTS5 实现 AND 多 token
        ///   - 纯拉丁/数字：小写并追加前缀通配符 *，让 "mi" 命中 "miyuji"
        /// </summary>
        private static string TransformSearchTermForUnicode61(string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return term;
            var trimmed = term.Trim();

            if (ChineseCharRegex.IsMatch(trimmed))
            {
                return SpaceOutCjkChars(trimmed);
            }

            // 纯拉丁/数字：使用 FTS5 前缀匹配
            var lower = trimmed.ToLowerInvariant();
            return lower.EndsWith("*") ? lower : lower + "*";
        }

        /// <summary>
        /// 由 Plugin 的 ItemAdded/ItemUpdated 事件调用：把 itemId 入队，用富化文本（拆字 + 全拼 + 首字母 + 后缀）覆盖 FTS 行。
        /// </summary>
        internal static void EnqueueFtsRefresh(long itemId)
        {
            if (itemId <= 0) return;
            if (_pendingFtsRefresh.TryAdd(itemId, 0))
                _pendingFtsRefreshOrder.Push(itemId);
            if (_enrichedIndexActive)
                SchedulePendingFtsRefresh();
        }

        private static void SchedulePendingFtsRefresh()
        {
            if (Interlocked.CompareExchange(ref _ftsRefreshScheduled, 1, 0) != 0) return;

            Task.Run(async () =>
            {
                try
                {
                    // 自适应调度：扫描中/队列积压时拉长间隔，给前台 Emby 查询/写入让路。
                    int delaySeconds;
                    if (Plugin.Instance.IsLibraryScanRunning)
                        delaySeconds = FtsRefreshScanDelaySeconds;
                    else if (_pendingFtsRefresh.Count >= FtsRefreshMaxRowsPerConnection)
                        delaySeconds = FtsRefreshBusyDelaySeconds;
                    else
                        delaySeconds = FtsRefreshIdleDelaySeconds;

                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);

                    // 扫描仍在进行：本轮不处理，让 Emby 独占写连接。下一轮若仍有堆积再来。
                    if (Plugin.Instance.IsLibraryScanRunning) return;

                    DrainPendingFtsRefreshWithNewConnection();
                }
                catch (Exception ex)
                {
                    Plugin.Instance.Logger.Warn(
                        $"EnhanceChineseSearch - Scheduled FTS refresh failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _ftsRefreshScheduled, 0);
                    if (!_pendingFtsRefresh.IsEmpty)
                        SchedulePendingFtsRefresh();
                }
            });
        }

        // 启动后立即运行一次（延迟 30 秒），之后每 15 分钟循环扫描 fts 缺失条目并入队刷新。
        // 停止：功能关闭或恢复时调用 StopCatchUpLoop()。
        private static void ScheduleStartupCatchUp()
        {
            _catchUpCts?.Cancel();
            _catchUpCts = new CancellationTokenSource();
            var token = _catchUpCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);

                    while (!token.IsCancellationRequested)
                    {
                        var repository = _libraryRepository;
                        // 扫描期间跳过 catch-up：LIMIT 10000 的 NOT EXISTS 查询在大库上会抢 library.db 资源。
                        if (repository != null && _createConnection != null && !Plugin.Instance.IsLibraryScanRunning)
                        {
                            try
                            {
                                using (var db = CreateWritableConnection(repository))
                                {
                                    if (db != null)
                                    {
                                        db.Execute("PRAGMA busy_timeout=1000");
                                        EnsureExtensionLoadedOnConnection(db);
                                        FindAndEnqueueMissingFtsItems(db);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Plugin.Instance.Logger.Warn(
                                    $"EnhanceChineseSearch - FTS catch-up scan failed: {ex.Message}");
                            }
                        }

                        await Task.Delay(TimeSpan.FromMinutes(15), token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        internal static void StopCatchUpLoop()
        {
            _catchUpCts?.Cancel();
            _catchUpCts = null;
        }

        internal static bool ScheduleRestoreFtsIndex()
        {
            if (!string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                return true;

            if (_libraryRepository == null || _createConnection == null)
                return false;

            if (Interlocked.CompareExchange(ref _restoreFtsScheduled, 1, 0) != 0)
                return true;

            Task.Run(() =>
            {
                try
                {
                    StopCatchUpLoop();
                    _enrichedIndexActive = false;

                    using (var db = CreateWritableConnection(_libraryRepository))
                    {
                        if (db == null)
                        {
                            Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Restore skipped: writable library db connection unavailable");
                            return;
                        }

                        db.Execute("PRAGMA busy_timeout=5000");
                        EnsureExtensionLoadedOnConnection(db);

                        var ftsTableName = AppVer >= Ver4830 ? "fts_search9" : "fts_search8";
                        if (RebuildFts(db, ftsTableName, "unicode61 remove_diacritics 2"))
                        {
                            CurrentTokenizerName = "unicode61 remove_diacritics 2";
                            _ftsTableName = ftsTableName;
                            Plugin.Instance.Logger.Info("EnhanceChineseSearch - Restore Success");
                            ResetOptions();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - Restore failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _restoreFtsScheduled, 0);
                }
            });

            return true;
        }

        // 查询 MediaItems 中不在 fts 表里的条目（服务器关闭期间外部工具入库的内容），入队增量刷新。
        private static void FindAndEnqueueMissingFtsItems(IDatabaseConnection db)
        {
            var ftsTableName = _ftsTableName;
            if (string.IsNullOrEmpty(ftsTableName)) return;

            var query = $@"SELECT mi.Id FROM MediaItems mi
                           WHERE mi.Name IS NOT NULL
                             AND NOT EXISTS (SELECT 1 FROM {ftsTableName} WHERE rowid = mi.Id)
                           LIMIT 10000";

            var count = 0;
            try
            {
                using (var stmt = db.PrepareStatement(query))
                {
                    while (stmt.MoveNext())
                    {
                        var idStr = stmt.Current.GetString(0);
                        if (long.TryParse(idStr, out var id))
                        {
                            if (_pendingFtsRefresh.TryAdd(id, 0))
                                _pendingFtsRefreshOrder.Push(id);
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn(
                    $"EnhanceChineseSearch - FindAndEnqueueMissingFtsItems failed: {ex.Message}");
                return;
            }

            if (count > 0)
            {
                Plugin.Instance.Logger.Info(
                    $"EnhanceChineseSearch - Startup catch-up: {count} items missing from FTS, queued for incremental refresh");
                SchedulePendingFtsRefresh();
            }
            else
            {
                Plugin.Instance.Logger.Info(
                    "EnhanceChineseSearch - Startup catch-up: all items present in FTS index");
            }
        }

        private static void DrainPendingFtsRefreshWithNewConnection()
        {
            var repository = _libraryRepository;
            if (repository == null || _createConnection == null) return;

            using (var db = CreateWritableConnection(repository))
            {
                if (db == null) return;
                // 降低 busy_timeout：与 Emby 写连接争锁时快速让步，避免拖住前台查询/刷新。
                db.Execute("PRAGMA busy_timeout=1000");
                EnsureExtensionLoadedOnConnection(db);
                DrainPendingFtsRefresh(db, FtsRefreshMaxRowsPerConnection);
            }
        }

        private static IDatabaseConnection CreateWritableConnection(object repository)
        {
            foreach (var args in GetCreateConnectionArgumentCandidates())
            {
                IDatabaseConnection db = null;
                try
                {
                    db = (IDatabaseConnection)_createConnection.Invoke(repository, args);
                    if (IsWritableConnection(db)) return db;
                }
                catch
                {
                }

                db?.Dispose();
            }

            return null;
        }

        private static IEnumerable<object[]> GetCreateConnectionArgumentCandidates()
        {
            var parameters = _createConnection.GetParameters();
            if (parameters.Length == 0)
            {
                yield return null;
                yield break;
            }

            var boolIndexes = parameters
                .Select((p, i) => new { Parameter = p, Index = i })
                .Where(x => x.Parameter.ParameterType == typeof(bool))
                .ToArray();

            var first = CreateConnectionArgs(parameters, false);
            foreach (var item in boolIndexes)
            {
                var name = item.Parameter.Name ?? string.Empty;
                if (name.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0)
                    first[item.Index] = true;
                if (name.IndexOf("read", StringComparison.OrdinalIgnoreCase) >= 0)
                    first[item.Index] = false;
            }
            yield return first;

            foreach (var item in boolIndexes)
            {
                var second = CreateConnectionArgs(parameters, false);
                second[item.Index] = !(bool)first[item.Index];
                yield return second;
            }
        }

        private static object[] CreateConnectionArgs(ParameterInfo[] parameters, bool boolDefault)
        {
            return parameters.Select(p =>
                p.HasDefaultValue ? p.DefaultValue :
                p.ParameterType == typeof(bool) ? (object)boolDefault :
                p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) :
                null).ToArray();
        }

        private static bool IsWritableConnection(IDatabaseConnection db)
        {
            try
            {
                using (var statement = db.PrepareStatement("PRAGMA query_only"))
                {
                    if (statement.MoveNext() && statement.Current.GetInt(0) != 0)
                        return false;
                }

                using (var statement = db.PrepareStatement("PRAGMA database_list"))
                {
                    while (statement.MoveNext())
                    {
                        var row = statement.Current;
                        if (string.Equals(row.GetString(1), "main", StringComparison.OrdinalIgnoreCase))
                        {
                            var path = row.GetString(2);
                            return !string.IsNullOrEmpty(path) && File.Exists(path) &&
                                   !new FileInfo(path).IsReadOnly;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance.DebugMode)
                    Plugin.Instance.Logger.Debug(
                        $"EnhanceChineseSearch - Writable connection check failed: {ex.Message}");
                return false;
            }

            return true;
        }

        private static void DrainPendingFtsRefresh(IDatabaseConnection db, int maxRows = FtsRefreshMaxRowsPerConnection)
        {
            if (!_enrichedIndexActive) return;
            if (_pendingFtsRefresh.IsEmpty) return;
            var ftsTableName = _ftsTableName;
            if (string.IsNullOrEmpty(ftsTableName)) return;
            if (db == null)
            {
                if (Plugin.Instance.DebugMode)
                    Plugin.Instance.Logger.Debug(
                        $"EnhanceChineseSearch - Pending FTS refresh waiting for library db connection ({_pendingFtsRefresh.Count} items)");
                return;
            }
            if (!IsWritableConnection(db))
            {
                if (Plugin.Instance.DebugMode)
                    Plugin.Instance.Logger.Debug(
                        $"EnhanceChineseSearch - Pending FTS refresh skipped readonly library db connection ({_pendingFtsRefresh.Count} items)");
                return;
            }

            // 抢锁失败时直接返回；下次搜索还会再尝试。
            if (!System.Threading.Monitor.TryEnter(_ftsRefreshLock)) return;
            try
            {
                if (_pendingFtsRefresh.IsEmpty) return;

                var ids = DequeuePendingFtsRefreshIds(Math.Max(1, maxRows));
                if (ids.Length == 0) return;

                var selectSql = AppVer < Ver4900
                    ? "SELECT Name, OriginalTitle, SeriesName, Album FROM MediaItems WHERE id = ?"
                    : "SELECT Name, OriginalTitle, SeriesName, " +
                      "(select case when AlbumId is null then null else (select name from MediaItems m2 where m2.Id = AlbumId limit 1) end)" +
                      " FROM MediaItems WHERE id = ?";
                var deleteSql = $"DELETE FROM {ftsTableName} WHERE rowid = ?";
                var insertSql =
                    $"INSERT INTO {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) VALUES (?,?,?,?,?)";
                var verifySql = $"SELECT rowid FROM {ftsTableName} WHERE {ftsTableName} MATCH ? AND rowid = ? LIMIT 1";

                // 批量路径用显式事务包住全部写入：1 次 fsync 代替 N 次，
                // 显著缩短我们持有 library.db 写锁的时间，避免拖慢 Emby 的前台查询与刷新写入。
                // 单行兜底路径（搜索热路径，maxRows<=1）不启事务，避免与 Emby 当前事务冲突。
                var useTransaction = maxRows > 1;
                var transactionStarted = false;
                if (useTransaction)
                {
                    try
                    {
                        db.Execute("BEGIN IMMEDIATE");
                        transactionStarted = true;
                    }
                    catch (Exception ex)
                    {
                        // 拿不到写锁就本轮放弃，等下次调度再来。
                        if (Plugin.Instance.DebugMode)
                            Plugin.Instance.Logger.Debug(
                                $"EnhanceChineseSearch - BEGIN IMMEDIATE failed, skipping this batch: {ex.Message}");
                        return;
                    }
                }

                var refreshed = 0;
                var committed = false;
                try
                {
                using (var selectStmt = db.PrepareStatement(selectSql))
                using (var deleteStmt = db.PrepareStatement(deleteSql))
                using (var insertStmt = db.PrepareStatement(insertSql))
                using (var verifyStmt = db.PrepareStatement(verifySql))
                {
                    foreach (var id in ids)
                    {
                        if (!_pendingFtsRefresh.ContainsKey(id)) continue;
                        var stage = "start";
                        try
                        {
                            stage = "select";
                            selectStmt.Reset();
                            selectStmt.BindParameters[0].Bind(id);
                            if (!selectStmt.MoveNext())
                            {
                                // MediaItems 已删除，让 Emby 自己的删除逻辑清理 fts 行即可。
                                _pendingFtsRefresh.TryRemove(id, out _);
                                continue;
                            }

                            var row = selectStmt.Current;
                            var name = row.GetString(0);
                            var originalTitle = row.GetString(1);
                            var seriesName = row.GetString(2);
                            var album = row.GetString(3);
                            var indexedName = NormalizeWithFullPinyin(name) ?? string.Empty;
                            var indexedOriginalTitle = NormalizeWithFullPinyin(originalTitle) ?? string.Empty;
                            var indexedSeriesName = NormalizeWithFullPinyin(seriesName) ?? string.Empty;
                            var indexedAlbum = NormalizeField(album) ?? string.Empty;

                            stage = "delete";
                            deleteStmt.Reset();
                            deleteStmt.BindParameters[0].Bind(id);
                            deleteStmt.MoveNext();

                            stage = "insert";
                            insertStmt.Reset();
                            insertStmt.BindParameters[0].Bind(id);
                            insertStmt.BindParameters[1].Bind(indexedName);
                            insertStmt.BindParameters[2].Bind(indexedOriginalTitle);
                            insertStmt.BindParameters[3].Bind(indexedSeriesName);
                            insertStmt.BindParameters[4].Bind(indexedAlbum);
                            insertStmt.MoveNext();

                            var initials = ConvertChineseToPinyinInitials(name);
                            var verifyTerm = string.IsNullOrEmpty(initials)
                                ? null
                                : initials.ToLowerInvariant() + "*";
                            var verified = false;
                            if (!string.IsNullOrEmpty(verifyTerm))
                            {
                                stage = "verify";
                                verifyStmt.Reset();
                                verifyStmt.BindParameters[0].Bind(verifyTerm);
                                verifyStmt.BindParameters[1].Bind(id);
                                verified = verifyStmt.MoveNext();
                            }

                            if (!verified && !string.IsNullOrEmpty(verifyTerm))
                            {
                                if (Plugin.Instance.DebugMode)
                                    Plugin.Instance.Logger.Debug(
                                        $"EnhanceChineseSearch - Incremental FTS verify failed for row {id}: Name='{name}', Initials='{initials}', Verify='{verifyTerm}', IndexedName='{TruncateForLog(indexedName, 160)}'");
                            }

                            _pendingFtsRefresh.TryRemove(id, out _);
                            refreshed++;
                        }
                        catch (Exception ex)
                        {
                            if (Plugin.Instance.DebugMode)
                                Plugin.Instance.Logger.Debug(
                                    $"EnhanceChineseSearch - Refresh fts row {id} failed at {stage}: {FormatExceptionForLog(ex)}");
                        }
                    }
                }

                if (transactionStarted)
                {
                    try
                    {
                        db.Execute("COMMIT");
                        committed = true;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Instance.Logger.Warn(
                            $"EnhanceChineseSearch - COMMIT failed: {ex.Message}");
                    }
                }

                if (refreshed > 0)
                    Plugin.Instance.Logger.Info(
                        $"EnhanceChineseSearch - Refreshed {refreshed} fts rows ({ftsTableName})");
                }
                finally
                {
                    if (transactionStarted && !committed)
                    {
                        try { db.Execute("ROLLBACK"); }
                        catch (Exception rbEx)
                        {
                            if (Plugin.Instance.DebugMode)
                                Plugin.Instance.Logger.Debug(
                                    $"EnhanceChineseSearch - ROLLBACK failed: {rbEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn(
                    $"EnhanceChineseSearch - DrainPendingFtsRefresh failed: {ex.Message}");
            }
            finally
            {
                System.Threading.Monitor.Exit(_ftsRefreshLock);
            }
        }

        private static string TruncateForLog(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength) + "...";
        }

        private static long[] DequeuePendingFtsRefreshIds(int maxRows)
        {
            var ids = new List<long>(maxRows);
            while (ids.Count < maxRows && _pendingFtsRefreshOrder.TryPop(out var id))
            {
                if (_pendingFtsRefresh.ContainsKey(id))
                    ids.Add(id);
            }

            return ids.ToArray();
        }

        private static string FormatExceptionForLog(Exception ex)
        {
            if (ex == null) return string.Empty;
            var inner = ex.InnerException == null
                ? string.Empty
                : $" | Inner={ex.InnerException.GetType().FullName}: {ex.InnerException.Message}";
            return $"{ex.GetType().FullName}: {ex.Message}{inner} | {TruncateForLog(ex.ToString(), 600)}";
        }

        [HarmonyPrefix]
        private static bool CacheIdsFromTextParamsPrefix(InternalItemsQuery query, IDatabaseConnection db)
        {
            EnsureExtensionLoadedOnConnection(db);
            DrainPendingFtsRefresh(db, 1);

            if ((query.PersonTypes?.Length ?? 0) == 0)
            {
                var nameStartsWith = query.NameStartsWith;
                if (!string.IsNullOrEmpty(nameStartsWith) && !_suppressSearchSuggestions)
                {
                    query.SearchTerm = nameStartsWith;
                    query.NameStartsWith = null;
                }

                var searchTerm = query.SearchTerm;
                var includeItemTypes = query.IncludeItemTypes ?? Array.Empty<string>();
                if (includeItemTypes.Length == 0 && !string.IsNullOrEmpty(searchTerm))
                {
                    query.IncludeItemTypes = GetSearchScope();
                }

                if (!string.IsNullOrEmpty(searchTerm))
                {
                    if (_digitsAsTmdbId && DigitsOnlyRegex.IsMatch(searchTerm.Trim()))
                    {
                        ApplyProviderIdFilter(query, "tmdb", searchTerm.Trim());
                    }
                    else
                    {
                        foreach (var provider in patterns)
                        {
                            var match = provider.Value.Match(searchTerm.Trim());
                            if (match.Success)
                            {
                                var idValue = provider.Key == "imdb" ? match.Value : match.Groups[2].Value;
                                ApplyProviderIdFilter(query, provider.Key, idValue);
                                break;
                            }
                        }
                    }
                }
            }

            return true;
        }

        private static void ApplyProviderIdFilter(InternalItemsQuery query, string providerId, string value)
        {
            query.HasAnyProviderId = new[] { providerId };
            query.AnyProviderIdEquals = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(providerId, value)
            };

            var searchScope = GetSearchScope() ?? Array.Empty<string>();
            var includeItemTypes = query.IncludeItemTypes ?? Array.Empty<string>();
            query.IncludeItemTypes = includeItemTypes.Length == 0
                ? searchScope
                : includeItemTypes.Intersect(searchScope, StringComparer.Ordinal).ToArray();
            query.SearchTerm = null;
        }
    }
}
