﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Flow.Launcher.Core.Plugin;
using Flow.Launcher.Core.Resource;
using Flow.Launcher.Helper;
using Flow.Launcher.Infrastructure;
using Flow.Launcher.Infrastructure.Hotkey;
using Flow.Launcher.Infrastructure.Storage;
using Flow.Launcher.Infrastructure.UserSettings;
using Flow.Launcher.Plugin;
using Flow.Launcher.Plugin.SharedCommands;
using Flow.Launcher.Storage;
using Flow.Launcher.Infrastructure.Logger;
using Microsoft.VisualStudio.Threading;
using System.Text;
using System.Threading.Channels;
using ISavable = Flow.Launcher.Plugin.ISavable;
using System.IO;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;

namespace Flow.Launcher.ViewModel
{
    public partial class MainViewModel : BaseModel, ISavable
    {
        #region Private Fields

        private bool _isQueryRunning;
        private Query _lastQuery;
        private string _queryTextBeforeLeaveResults;

        private readonly FlowLauncherJsonStorage<History> _historyItemsStorage;
        private readonly FlowLauncherJsonStorage<UserSelectedRecord> _userSelectedRecordStorage;
        private readonly FlowLauncherJsonStorage<TopMostRecord> _topMostRecordStorage;
        private readonly History _history;
        private readonly UserSelectedRecord _userSelectedRecord;
        private readonly TopMostRecord _topMostRecord;

        private CancellationTokenSource _updateSource;
        private CancellationToken _updateToken;

        private readonly Internationalization _translator = InternationalizationManager.Instance;

        private ChannelWriter<ResultsForUpdate> _resultsUpdateChannelWriter;
        private Task _resultsViewUpdateTask;

        #endregion

        #region Constructor

        public MainViewModel(Settings settings)
        {
            _queryTextBeforeLeaveResults = "";
            _queryText = "";
            _lastQuery = new Query();

            Settings = settings;
            Settings.PropertyChanged += (_, args) =>
            {
                switch (args.PropertyName)
                {
                    case nameof(Settings.WindowSize):
                        OnPropertyChanged(nameof(MainWindowWidth));
                        break;
                    case nameof(Settings.AlwaysStartEn):
                        OnPropertyChanged(nameof(StartWithEnglishMode));
                        break;
                    case nameof(Settings.OpenResultModifiers):
                        OnPropertyChanged(nameof(OpenResultCommandModifiers));
                        break;
                }
            };

            _historyItemsStorage = new FlowLauncherJsonStorage<History>();
            _userSelectedRecordStorage = new FlowLauncherJsonStorage<UserSelectedRecord>();
            _topMostRecordStorage = new FlowLauncherJsonStorage<TopMostRecord>();
            _history = _historyItemsStorage.Load();
            _userSelectedRecord = _userSelectedRecordStorage.Load();
            _topMostRecord = _topMostRecordStorage.Load();


            ContextMenu = new ResultsViewModel(Settings)
            {
                LeftClickResultCommand = OpenResultCommand, RightClickResultCommand = LoadContextMenuCommand
            };
            Results = new ResultsViewModel(Settings)
            {
                LeftClickResultCommand = OpenResultCommand, RightClickResultCommand = LoadContextMenuCommand
            };
            History = new ResultsViewModel(Settings)
            {
                LeftClickResultCommand = OpenResultCommand, RightClickResultCommand = LoadContextMenuCommand
            };
            _selectedResults = Results;


            RegisterViewUpdate();
            RegisterResultsUpdatedEvent();
            _ = RegisterClockAndDateUpdateAsync();
        }

        private void RegisterViewUpdate()
        {
            var resultUpdateChannel = Channel.CreateUnbounded<ResultsForUpdate>();
            _resultsUpdateChannelWriter = resultUpdateChannel.Writer;
            _resultsViewUpdateTask =
                Task.Run(updateAction).ContinueWith(continueAction, TaskContinuationOptions.OnlyOnFaulted);

            async Task updateAction()
            {
                var queue = new Dictionary<string, ResultsForUpdate>();
                var channelReader = resultUpdateChannel.Reader;

                // it is not supposed to be false because it won't be complete
                while (await channelReader.WaitToReadAsync())
                {
                    await Task.Delay(20);
                    while (channelReader.TryRead(out var item))
                    {
                        if (!item.Token.IsCancellationRequested)
                            queue[item.ID] = item;
                    }

                    UpdateResultView(queue.Values);
                    queue.Clear();
                }

                Log.Error("MainViewModel", "Unexpected ResultViewUpdate ends");
            }

            void continueAction(Task t)
            {
#if DEBUG
                throw t.Exception;
#else
                Log.Error($"Error happen in task dealing with viewupdate for results. {t.Exception}");
                _resultsViewUpdateTask =
                    Task.Run(updateAction).ContinueWith(continueAction, TaskContinuationOptions.OnlyOnFaulted);
#endif
            }
        }

        private void RegisterResultsUpdatedEvent()
        {
            foreach (var pair in PluginManager.GetPluginsForInterface<IResultUpdated>())
            {
                var plugin = (IResultUpdated)pair.Plugin;
                plugin.ResultsUpdated += (s, e) =>
                {
                    if (e.Query.RawQuery != QueryText || e.Token.IsCancellationRequested)
                    {
                        return;
                    }

                    var token = e.Token == default ? _updateToken : e.Token;

                    PluginManager.UpdatePluginMetadata(e.Results, pair.Metadata, e.Query);
                    if (!_resultsUpdateChannelWriter.TryWrite(new ResultsForUpdate(e.Results, pair.Metadata, e.Query, token)))
                    {
                        Log.Error("MainViewModel", "Unable to add item to Result Update Queue");
                    }
                };
            }
        }

        [RelayCommand]
        private async Task ReloadPluginDataAsync()
        {
            Hide();

            await PluginManager.ReloadDataAsync().ConfigureAwait(false);
            Notification.Show(InternationalizationManager.Instance.GetTranslation("success"), InternationalizationManager.Instance.GetTranslation("completedSuccessfully"));
        }

        [RelayCommand]
        private void LoadHistory()
        {
            if (SelectedIsFromQueryResults())
            {
                SelectedResults = History;
                History.SelectedIndex = _history.Items.Count - 1;
            }
            else
            {
                SelectedResults = Results;
            }
        }

        [RelayCommand]
        private void LoadContextMenu()
        {
            if (SelectedIsFromQueryResults())
            {
                // When switch to ContextMenu from QueryResults, but no item being chosen, should do nothing
                // i.e. Shift+Enter/Ctrl+O right after Alt + Space should do nothing
                if (SelectedResults.SelectedItem != null)
                    SelectedResults = ContextMenu;
            }
            else
            {
                SelectedResults = Results;
            }
        }

        [RelayCommand]
        private void Backspace(object index)
        {
            var query = QueryBuilder.Build(QueryText.Trim(), PluginManager.NonGlobalPlugins);

            // GetPreviousExistingDirectory does not require trailing '\', otherwise will return empty string
            var path = FilesFolders.GetPreviousExistingDirectory((_) => true, query.Search.TrimEnd('\\'));

            var actionKeyword = string.IsNullOrEmpty(query.ActionKeyword) ? string.Empty : $"{query.ActionKeyword} ";

            ChangeQueryText($"{actionKeyword}{path}");
        }

        [RelayCommand]
        private void AutocompleteQuery()
        {
            var result = SelectedResults.SelectedItem?.Result;
            if (result != null && SelectedIsFromQueryResults()) // SelectedItem returns null if selection is empty.
            {
                var autoCompleteText = result.Title;

                if (!string.IsNullOrEmpty(result.AutoCompleteText))
                {
                    autoCompleteText = result.AutoCompleteText;
                }
                else if (!string.IsNullOrEmpty(SelectedResults.SelectedItem?.QuerySuggestionText))
                {
                    autoCompleteText = SelectedResults.SelectedItem.QuerySuggestionText;
                }

                var specialKeyState = GlobalHotkey.CheckModifiers();
                if (specialKeyState.ShiftPressed)
                {
                    autoCompleteText = result.SubTitle;
                }

                ChangeQueryText(autoCompleteText);
            }
        }

        [RelayCommand]
        private async Task OpenResultAsync(string index)
        {
            var results = SelectedResults;
            if (index is not null)
            {
                results.SelectedIndex = int.Parse(index);
            }
            var result = results.SelectedItem?.Result;
            if (result == null)
            {
                return;
            }
            var hideWindow = await result.ExecuteAsync(new ActionContext
                {
                    SpecialKeyState = GlobalHotkey.CheckModifiers()
                })
                .ConfigureAwait(false);

            if (hideWindow)
            {
                Hide();
            }

            if (SelectedIsFromQueryResults())
            {
                _userSelectedRecord.Add(result);
                _history.Add(result.OriginQuery.RawQuery);
            }
            else
            {
                SelectedResults = Results;
            }
        }

        [RelayCommand]
        private void OpenSetting()
        {
            App.API.OpenSettingDialog();
        }

        [RelayCommand]
        private void SelectHelp()
        {
            PluginManager.API.OpenUrl("https://www.flowlauncher.com/docs/#/usage-tips");
        }

        [RelayCommand]
        private void SelectFirstResult()
        {
            SelectedResults.SelectFirstResult();
        }

        [RelayCommand]
        private void SelectPrevPage()
        {
            SelectedResults.SelectPrevPage();
        }

        [RelayCommand]
        private void SelectNextPage()
        {
            SelectedResults.SelectNextPage();
        }

        [RelayCommand]
        private void SelectPrevItem()
        {
            SelectedResults.SelectPrevResult();
        }

        [RelayCommand]
        private void SelectNextItem()
        {
            SelectedResults.SelectNextResult();
        }

        [RelayCommand]
        private void Esc()
        {
            if (!SelectedIsFromQueryResults())
            {
                SelectedResults = Results;
            }
            else
            {
                Hide();
            }
        }

        #endregion

        #region ViewModel Properties

        public Settings Settings { get; }
        public string ClockText { get; private set; }
        public string DateText { get; private set; }
        public CultureInfo Culture => CultureInfo.DefaultThreadCurrentCulture;

        private async Task RegisterClockAndDateUpdateAsync()
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            // ReSharper disable once MethodSupportsCancellation
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                if (Settings.UseClock)
                    ClockText = DateTime.Now.ToString(Settings.TimeFormat, Culture);
                if (Settings.UseDate)
                    DateText = DateTime.Now.ToString(Settings.DateFormat, Culture);
            }
        }

        public ResultsViewModel Results { get; private set; }

        public ResultsViewModel ContextMenu { get; private set; }

        public ResultsViewModel History { get; private set; }

        public bool GameModeStatus { get; set; }

        private string _queryText;
        public string QueryText
        {
            get => _queryText;
            set
            {
                _queryText = value;
                OnPropertyChanged();
                Query();
            }
        }


        [RelayCommand]
        private void IncreaseWidth()
        {
            if (MainWindowWidth + 100 > 1920 || Settings.WindowSize == 1920)
            {
                Settings.WindowSize = 1920;
            }
            else
            {
                Settings.WindowSize += 100;
                Settings.WindowLeft -= 50;
            }
            OnPropertyChanged();
        }

        [RelayCommand]
        private void DecreaseWidth()
        {
            if (MainWindowWidth - 100 < 400 || Settings.WindowSize == 400)
            {
                Settings.WindowSize = 400;
            }
            else
            {
                Settings.WindowLeft += 50;
                Settings.WindowSize -= 100;
            }
            OnPropertyChanged();
        }

        [RelayCommand]
        private void IncreaseMaxResult()
        {
            if (Settings.MaxResultsToShow == 17)
                return;

            Settings.MaxResultsToShow += 1;
        }

        [RelayCommand]
        private void DecreaseMaxResult()
        {
            if (Settings.MaxResultsToShow == 2)
                return;

            Settings.MaxResultsToShow -= 1;
        }

        /// <summary>
        /// we need move cursor to end when we manually changed query
        /// but we don't want to move cursor to end when query is updated from TextBox
        /// </summary>
        /// <param name="queryText"></param>
        /// <param name="reQuery">Force query even when Query Text doesn't change</param>
        public void ChangeQueryText(string queryText, bool reQuery = false)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (QueryText != queryText)
                {

                    // re-query is done in QueryText's setter method
                    QueryText = queryText;
                    // set to false so the subsequent set true triggers
                    // PropertyChanged and MoveQueryTextToEnd is called
                    QueryTextCursorMovedToEnd = false;

                }
                else if (reQuery)
                {
                    Query();
                }
                QueryTextCursorMovedToEnd = true;
            });
        }

        public bool LastQuerySelected { get; set; }

        // This is not a reliable indicator of the cursor's position, it is manually set for a specific purpose.
        public bool QueryTextCursorMovedToEnd { get; set; }

        private ResultsViewModel _selectedResults;

        private ResultsViewModel SelectedResults
        {
            get { return _selectedResults; }
            set
            {
                _selectedResults = value;
                if (SelectedIsFromQueryResults())
                {
                    ContextMenu.Visbility = Visibility.Collapsed;
                    History.Visbility = Visibility.Collapsed;
                    ChangeQueryText(_queryTextBeforeLeaveResults);
                }
                else
                {
                    Results.Visbility = Visibility.Collapsed;
                    _queryTextBeforeLeaveResults = QueryText;


                    // Because of Fody's optimization
                    // setter won't be called when property value is not changed.
                    // so we need manually call Query()
                    // http://stackoverflow.com/posts/25895769/revisions
                    if (string.IsNullOrEmpty(QueryText))
                    {
                        Query();
                    }
                    else
                    {
                        QueryText = string.Empty;
                    }
                }

                _selectedResults.Visbility = Visibility.Visible;
            }
        }

        public Visibility ProgressBarVisibility { get; set; }
        public Visibility MainWindowVisibility { get; set; }
        public double MainWindowOpacity { get; set; } = 1;

        // This is to be used for determining the visibility status of the mainwindow instead of MainWindowVisibility
        // because it is more accurate and reliable representation than using Visibility as a condition check
        public bool MainWindowVisibilityStatus { get; set; } = true;

        public Visibility SearchIconVisibility { get; set; }

        public double MainWindowWidth
        {
            get => Settings.WindowSize;
            set => Settings.WindowSize = value;
        }

        public string PluginIconPath { get; set; } = null;

        public string OpenResultCommandModifiers => Settings.OpenResultModifiers;

        public string Image => Constant.QueryTextBoxIconImagePath;

        public bool StartWithEnglishMode => Settings.AlwaysStartEn;

        #endregion

        #region Query

        public void Query()
        {
            if (SelectedIsFromQueryResults())
            {
                QueryResults();
            }
            else if (ContextMenuSelected())
            {
                QueryContextMenu();
            }
            else if (HistorySelected())
            {
                QueryHistory();
            }
        }

        private void QueryContextMenu()
        {
            const string id = "Context Menu ID";
            var query = QueryText.ToLower().Trim();
            ContextMenu.Clear();

            var selected = Results.SelectedItem?.Result;

            if (selected != null) // SelectedItem returns null if selection is empty.
            {
                var results = PluginManager.GetContextMenusForPlugin(selected);
                results.Add(ContextMenuTopMost(selected));
                results.Add(ContextMenuPluginInfo(selected.PluginID));

                if (!string.IsNullOrEmpty(query))
                {
                    var filtered = results.Where
                    (
                        r =>
                        {
                            var match = StringMatcher.FuzzySearch(query, r.Title);
                            if (!match.IsSearchPrecisionScoreMet())
                            {
                                match = StringMatcher.FuzzySearch(query, r.SubTitle);
                            }

                            if (!match.IsSearchPrecisionScoreMet()) return false;

                            r.Score = match.Score;
                            return true;

                        }).ToList();
                    ContextMenu.AddResults(filtered, id);
                }
                else
                {
                    ContextMenu.AddResults(results, id);
                }
            }
        }

        private void QueryHistory()
        {
            const string id = "Query History ID";
            var query = QueryText.ToLower().Trim();
            History.Clear();

            var results = new List<Result>();
            foreach (var h in _history.Items)
            {
                var title = _translator.GetTranslation("executeQuery");
                var time = _translator.GetTranslation("lastExecuteTime");
                var result = new Result
                {
                    Title = string.Format(title, h.Query),
                    SubTitle = string.Format(time, h.ExecutedDateTime),
                    IcoPath = "Images\\history.png",
                    OriginQuery = new Query
                    {
                        RawQuery = h.Query
                    },
                    Action = _ =>
                    {
                        SelectedResults = Results;
                        ChangeQueryText(h.Query);
                        return false;
                    }
                };
                results.Add(result);
            }

            if (!string.IsNullOrEmpty(query))
            {
                var filtered = results.Where
                (
                    r => StringMatcher.FuzzySearch(query, r.Title).IsSearchPrecisionScoreMet() ||
                         StringMatcher.FuzzySearch(query, r.SubTitle).IsSearchPrecisionScoreMet()
                ).ToList();
                History.AddResults(filtered, id);
            }
            else
            {
                History.AddResults(results, id);
            }
        }

        private readonly IReadOnlyList<Result> _emptyResult = new List<Result>();

        private async void QueryResults()
        {
            _updateSource?.Cancel();

            var query = ConstructQuery(QueryText, Settings.CustomShortcuts, Settings.BuiltinShortcuts);

            if (query == null) // shortcut expanded
            {
                Results.Clear();
                Results.Visbility = Visibility.Collapsed;
                PluginIconPath = null;
                SearchIconVisibility = Visibility.Visible;
                return;
            }

            _updateSource?.Dispose();

            var currentUpdateSource = new CancellationTokenSource();
            _updateSource = currentUpdateSource;
            var currentCancellationToken = _updateSource.Token;
            _updateToken = currentCancellationToken;

            ProgressBarVisibility = Visibility.Hidden;
            _isQueryRunning = true;

            // Switch to ThreadPool thread
            await TaskScheduler.Default;

            if (currentCancellationToken.IsCancellationRequested)
                return;


            // handle the exclusiveness of plugin using action keyword
            RemoveOldQueryResults(query);

            _lastQuery = query;

            var plugins = PluginManager.ValidPluginsForQuery(query);

            if (plugins.Count == 1)
            {
                PluginIconPath = plugins.Single().Metadata.IcoPath;
                SearchIconVisibility = Visibility.Hidden;
            }
            else
            {
                PluginIconPath = null;
                SearchIconVisibility = Visibility.Visible;
            }


            if (query.ActionKeyword == Plugin.Query.GlobalPluginWildcardSign)
            {
                // Wait 45 millisecond for query change in global query
                // if query changes, return so that it won't be calculated
                await Task.Delay(45, currentCancellationToken);
                if (currentCancellationToken.IsCancellationRequested)
                    return;
            }

            _ = Task.Delay(200, currentCancellationToken).ContinueWith(_ =>
            {
                // start the progress bar if query takes more than 200 ms and this is the current running query and it didn't finish yet
                if (!currentCancellationToken.IsCancellationRequested && _isQueryRunning)
                {
                    ProgressBarVisibility = Visibility.Visible;
                }
            }, currentCancellationToken, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);

            // plugins is ICollection, meaning LINQ will get the Count and preallocate Array

            var tasks = plugins.Select(plugin => plugin.Metadata.Disabled switch
            {
                false => QueryTask(plugin),
                true => Task.CompletedTask
            }).ToArray();


            try
            {
                // Check the code, WhenAll will translate all type of IEnumerable or Collection to Array, so make an array at first
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // nothing to do here
            }

            if (currentCancellationToken.IsCancellationRequested)
                return;

            // this should happen once after all queries are done so progress bar should continue
            // until the end of all querying
            _isQueryRunning = false;
            if (!currentCancellationToken.IsCancellationRequested)
            {
                // update to hidden if this is still the current query
                ProgressBarVisibility = Visibility.Hidden;
            }

            // Local function
            async Task QueryTask(PluginPair plugin)
            {
                // Since it is wrapped within a ThreadPool Thread, the synchronous context is null
                // Task.Yield will force it to run in ThreadPool
                await Task.Yield();

                IReadOnlyList<Result> results = await PluginManager.QueryForPluginAsync(plugin, query, currentCancellationToken);

                currentCancellationToken.ThrowIfCancellationRequested();

                results ??= _emptyResult;

                if (!_resultsUpdateChannelWriter.TryWrite(new ResultsForUpdate(results, plugin.Metadata, query, currentCancellationToken)))
                {
                    Log.Error("MainViewModel", "Unable to add item to Result Update Queue");
                }
            }
        }

        private Query ConstructQuery(string queryText, IEnumerable<CustomShortcutModel> customShortcuts, IEnumerable<BuiltinShortcutModel> builtInShortcuts)
        {
            if (string.IsNullOrWhiteSpace(queryText))
            {
                return null;
            }

            StringBuilder queryBuilder = new(queryText);
            StringBuilder queryBuilderTmp = new(queryText);

            foreach (var shortcut in customShortcuts)
            {
                if (queryBuilder.Equals(shortcut.Key))
                {
                    queryBuilder.Replace(shortcut.Key, shortcut.Expand());
                }

                queryBuilder.Replace('@' + shortcut.Key, shortcut.Expand());
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var shortcut in builtInShortcuts)
                {
                    queryBuilder.Replace(shortcut.Key, shortcut.Expand());
                    queryBuilderTmp.Replace(shortcut.Key, shortcut.Expand());
                }
            });

            // show expanded builtin shortcuts
            // use private field to avoid infinite recursion
            _queryText = queryBuilderTmp.ToString();

            var query = QueryBuilder.Build(queryBuilder.ToString().Trim(), PluginManager.NonGlobalPlugins);
            return query;
        }

        private void RemoveOldQueryResults(Query query)
        {
            if (_lastQuery?.ActionKeyword != query?.ActionKeyword)
            {
                Results.Clear();
            }
        }

        private Result ContextMenuTopMost(Result result)
        {
            Result menu;
            if (_topMostRecord.IsTopMost(result))
            {
                menu = new Result
                {
                    Title = InternationalizationManager.Instance.GetTranslation("cancelTopMostInThisQuery"),
                    IcoPath = "Images\\down.png",
                    PluginDirectory = Constant.ProgramDirectory,
                    Action = _ =>
                    {
                        _topMostRecord.Remove(result);
                        App.API.ShowMsg(InternationalizationManager.Instance.GetTranslation("success"));
                        return false;
                    }
                };
            }
            else
            {
                menu = new Result
                {
                    Title = InternationalizationManager.Instance.GetTranslation("setAsTopMostInThisQuery"),
                    IcoPath = "Images\\up.png",
                    Glyph = new GlyphInfo(FontFamily: "/Resources/#Segoe Fluent Icons", Glyph: "\xeac2"),
                    PluginDirectory = Constant.ProgramDirectory,
                    Action = _ =>
                    {
                        _topMostRecord.AddOrUpdate(result);
                        App.API.ShowMsg(InternationalizationManager.Instance.GetTranslation("success"));
                        return false;
                    }
                };
            }

            return menu;
        }

        private Result ContextMenuPluginInfo(string id)
        {
            var metadata = PluginManager.GetPluginForId(id).Metadata;
            var translator = InternationalizationManager.Instance;

            var author = translator.GetTranslation("author");
            var website = translator.GetTranslation("website");
            var version = translator.GetTranslation("version");
            var plugin = translator.GetTranslation("plugin");
            var title = $"{plugin}: {metadata.Name}";
            var icon = metadata.IcoPath;
            var subtitle = $"{author} {metadata.Author}";

            var menu = new Result
            {
                Title = title,
                IcoPath = icon,
                SubTitle = subtitle,
                PluginDirectory = metadata.PluginDirectory,
                Action = _ =>
                {
                    App.API.OpenUrl(metadata.Website);
                    return true;
                }
            };
            return menu;
        }

        internal bool SelectedIsFromQueryResults()
        {
            var selected = SelectedResults == Results;
            return selected;
        }

        private bool ContextMenuSelected()
        {
            var selected = SelectedResults == ContextMenu;
            return selected;
        }

        private bool HistorySelected()
        {
            var selected = SelectedResults == History;
            return selected;
        }

        #endregion

        #region Hotkey

        public void ToggleFlowLauncher()
        {
            if (!MainWindowVisibilityStatus)
            {
                Show();
            }
            else
            {
                Hide();
            }
        }

        public void Show()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                MainWindowVisibility = Visibility.Visible;

                MainWindowOpacity = 1;

                MainWindowVisibilityStatus = true;
            });
        }

        public async void Hide()
        {
            // Trick for no delay
            MainWindowOpacity = 0;

            switch (Settings.LastQueryMode)
            {
                case LastQueryMode.Empty:
                    ChangeQueryText(string.Empty);
                    await Task.Delay(100); //Time for change to opacity
                    break;
                case LastQueryMode.Preserved:
                    if (Settings.UseAnimation)
                        await Task.Delay(100);
                    LastQuerySelected = true;
                    break;
                case LastQueryMode.Selected:
                    if (Settings.UseAnimation)
                        await Task.Delay(100);
                    LastQuerySelected = false;
                    break;
                default:
                    throw new ArgumentException($"wrong LastQueryMode: <{Settings.LastQueryMode}>");
            }

            MainWindowVisibilityStatus = false;
            MainWindowVisibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Checks if Flow Launcher should ignore any hotkeys
        /// </summary>
        public bool ShouldIgnoreHotkeys()
        {
            return Settings.IgnoreHotkeysOnFullscreen && WindowsInteropHelper.IsWindowFullscreen();
        }

        #endregion

        #region Public Methods

        public void Save()
        {
            _historyItemsStorage.Save();
            _userSelectedRecordStorage.Save();
            _topMostRecordStorage.Save();
        }

        /// <summary>
        /// To avoid deadlock, this method should not called from main thread
        /// </summary>
        public void UpdateResultView(IEnumerable<ResultsForUpdate> resultsForUpdates)
        {
            if (!resultsForUpdates.Any())
                return;
            CancellationToken token;

            try
            {
                // Don't know why sometimes even resultsForUpdates is empty, the method won't return;
                token = resultsForUpdates.Select(r => r.Token).Distinct().SingleOrDefault();
            }
#if DEBUG
            catch
            {
                throw new ArgumentException("Unacceptable token");
            }
#else
            catch
            {
                token = default;
            }
#endif

            foreach (var metaResults in resultsForUpdates)
            {
                foreach (var result in metaResults.Results)
                {
                    if (_topMostRecord.IsTopMost(result))
                    {
                        result.Score = int.MaxValue;
                    }
                    else
                    {
                        var priorityScore = metaResults.Metadata.Priority * 150;
                        result.Score += _userSelectedRecord.GetSelectedCount(result) + priorityScore;
                    }
                }
            }

            Results.AddResults(resultsForUpdates, token);
        }

        /// <summary>
        /// This is the global copy method for an individual result. If no text is passed, 
        /// the method will work out what is to be copied based on the result, so plugin can offer the text 
        /// to be copied via the result model. If the text is a directory/file path, 
        /// then actual file/folder will be copied instead. 
        /// The result's subtitle text is the default text to be copied
        /// </summary>
        public void ResultCopy(string stringToCopy)
        {
            if (string.IsNullOrEmpty(stringToCopy))
            {
                var result = Results.SelectedItem?.Result;
                if (result != null)
                {
                    string copyText = result.CopyText;
                    var isFile = File.Exists(copyText);
                    var isFolder = Directory.Exists(copyText);
                    if (isFile || isFolder)
                    {
                        var paths = new StringCollection
                        {
                            copyText
                        };

                        Clipboard.SetFileDropList(paths);
                        App.API.ShowMsg(
                            $"{App.API.GetTranslation("copy")} {(isFile ? App.API.GetTranslation("fileTitle") : App.API.GetTranslation("folderTitle"))}",
                            App.API.GetTranslation("completedSuccessfully"));
                    }
                    else
                    {
                        Clipboard.SetDataObject(copyText);
                        App.API.ShowMsg(
                            $"{App.API.GetTranslation("copy")} {App.API.GetTranslation("textTitle")}",
                            App.API.GetTranslation("completedSuccessfully"));
                    }
                }

                return;
            }

            Clipboard.SetDataObject(stringToCopy);
        }

        #endregion
    }
}
