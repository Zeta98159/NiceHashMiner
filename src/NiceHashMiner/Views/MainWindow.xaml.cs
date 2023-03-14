﻿using NHM.Common;
using NHMCore;
using NHMCore.ApplicationState;
using NHMCore.Configs;
using NHMCore.Mining.Plugins;
using NHMCore.Notifications;
using NHMCore.Utils;
using NiceHashMiner.ViewModels;
using NiceHashMiner.Views.Common;
using NiceHashMiner.Views.Common.NHBase;
using NiceHashMiner.Views.TDPSettings;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using Windows.ApplicationModel.VoiceCommands;

namespace NiceHashMiner.Views
{
    /// <summary>
    /// Interaction logic for MainWindowNew2.xaml
    /// </summary>
    public partial class MainWindow : NHMMainWindow
    {
        private readonly MainVM _vm;
        private bool _miningStoppedOnClose;
        private Timer _timer = new Timer();

        public readonly bool? LoginSuccess = null;

        public MainWindow(bool? loginSuccess)
        {
            InitializeComponent();
            LoginSuccess = loginSuccess;
            _vm = this.AssertViewModel<MainVM>();
            Title = ApplicationStateManager.Title;

            base.SizeChanged += new SizeChangedEventHandler(this.OnSizeChangedSave);

            Translations.LanguageChanged += (s, e) => WindowUtils.Translate(this);
            LoadingBar.Visibility = Visibility.Visible;
            Topmost = GUISettings.Instance.GUIWindowsAlwaysOnTop;
            CustomDialogManager.MainWindow = this;
            SetBurnCalledAction();
            SetNoDeviceAction();
            _timer.Interval = 1000 * 60 * 2; //2min
            _timer.Elapsed += CheckConnection;
            _timer.Start();

            if (GUISettings.Instance.MainFormSize != System.Drawing.Size.Empty)
            {
                this.Width = GUISettings.Instance.MainFormSize.Width;
                this.Height = GUISettings.Instance.MainFormSize.Height;
            }
        }

        private void OnSizeChangedSave(object sender, SizeChangedEventArgs e)
        {
            GUISettings.Instance.MainFormSize = new System.Drawing.Size((int)e.NewSize.Width, (int)e.NewSize.Height);
        }

        private void GUISettings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GUISettings.GUIWindowsAlwaysOnTop))
            {
                this.Topmost = _vm.GUISettings.GUIWindowsAlwaysOnTop;
            }
        }

        #region Start-Loaded/Closing
        private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            SetBuildTag();
            ThemeSetterManager.SetThemeSelectedThemes();
            UpdateHelpers.OnAutoUpdate = () =>
            {
                this.Dispatcher.Invoke(() =>
                {
                    var nhmUpdaterDialog = new CustomDialog()
                    {
                        Title = Translations.Tr("NiceHash Miner Starting Update"),
                        Description = Translations.Tr("NiceHash Miner auto updater in progress."),
                        CancelVisible = Visibility.Collapsed,
                        OkVisible = Visibility.Collapsed,
                        AnimationVisible = Visibility.Visible,
                        ExitVisible = Visibility.Collapsed
                    };
                    ShowContentAsModalDialog(nhmUpdaterDialog);
                });
            };
            await MainWindow_OnLoadedTask();
            _vm.GUISettings.PropertyChanged += GUISettings_PropertyChanged;
            NotificationsManager.Instance.PropertyChanged += NotificationsManagerInstance_PropertyChanged;
            MiningState.Instance.PropertyChanged += MiningStateInstance_PropertyChanged;
            MiscSettings.Instance.PropertyChanged += MiscSettings_PropertyChanged_HandleELPTabVisibility;
            SetELPTabVisibilityAccordingToSettings();
            SetNotificationCount(NotificationsManager.Instance.NotificationNewCount);

            if (!HasWriteAccessToFolder(Paths.Root))
            {
                this.Dispatcher.Invoke(() =>
                {
                    var nhmNoPermissions = new CustomDialog()
                    {
                        Title = Translations.Tr("Folder lacks permissions"),
                        Description = Translations.Tr("NiceHash Miner folder doesn't have write access. This can prevent some features from working."),
                        OkText = Translations.Tr("OK"),
                        CancelVisible = Visibility.Collapsed,
                        OkVisible = Visibility.Visible,
                        AnimationVisible = Visibility.Collapsed
                    };
                    ShowContentAsModalDialog(nhmNoPermissions);
                });
            }
        }
        private void MiscSettings_PropertyChanged_HandleELPTabVisibility(object sender, PropertyChangedEventArgs e)
        {
            SetELPTabVisibilityAccordingToSettings();
        }
        private void SetELPTabVisibilityAccordingToSettings()
        {
            if (MiscSettings.Instance.AdvancedMode) EnableELPTabButton();
            else DisableELPTabButton();
        }

        private void MiningStateInstance_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (nameof(MiningState.Instance.IsDemoMining) == e.PropertyName && MiningState.Instance.IsDemoMining)
            {
                Dispatcher.Invoke(() =>
                {
                    var demoMiningDialog = new EnterWalletDialogDemo();
                    CustomDialogManager.ShowModalDialog(demoMiningDialog);
                });
            }
        }

        private void NotificationsManagerInstance_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (nameof(NotificationsManager.NotificationNewCount) == e.PropertyName)
            {
                Dispatcher.Invoke(() =>
                {
                    SetNotificationCount(NotificationsManager.Instance.NotificationNewCount);
                });
            }
        }

        public void SetBurnCalledAction()
        {
            ApplicationStateManager.BurnCalledAction = () =>
            {
                this.Dispatcher.Invoke(() =>
                {
                    var nhmBurnDialog = new CustomDialog()
                    {
                        Title = Translations.Tr("Burn Error!"),
                        Description = Translations.Tr("Error during burn"),
                        OkText = Translations.Tr("OK"),
                        CancelVisible = Visibility.Collapsed,
                        AnimationVisible = Visibility.Collapsed
                    };
                    nhmBurnDialog.OnExit += (s, e) =>
                    {
                        ApplicationStateManager.ExecuteApplicationExit();
                    };
                    ShowContentAsModalDialog(nhmBurnDialog);
                });
            };
        }

        public void SetNoDeviceAction()
        {
            ApplicationStateManager.NoDeviceAction = () =>
            {
                this.Dispatcher.Invoke(() =>
                {
                    var nhmNoDeviceDialog = new CustomDialog()
                    {
                        Title = Translations.Tr("No Supported Devices"),
                        Description = Translations.Tr("No supported devices are found. Select the OK button for help or cancel to continue."),
                        OkText = Translations.Tr("OK"),
                        CancelText = Translations.Tr("Cancel"),
                        AnimationVisible = Visibility.Collapsed
                    };
                    nhmNoDeviceDialog.OKClick += (s, e) =>
                    {
                        Helpers.VisitUrlLink(Links.NhmNoDevHelp);
                    };
                    nhmNoDeviceDialog.OnExit += (s, e) =>
                    {
                        ApplicationStateManager.ExecuteApplicationExit();
                    };
                    ShowContentAsModalDialog(nhmNoDeviceDialog);
                });
            };
        }

        // just in case we add more awaits this signature will await all of them
        private async Task MainWindow_OnLoadedTask()
        {
            try
            {
                await _vm.InitializeNhm(LoadingBar.StartupLoader);
            }
            finally
            {
                LoadingBar.Visibility = Visibility.Collapsed;
                var showMainEULA = ToSSetings.Instance.AgreedWithTOS != ApplicationStateManager.CurrentTosVer;
                var show3rdParty = ToSSetings.Instance.Use3rdPartyMinersTOS != ApplicationStateManager.CurrentTosVer;
                EulaMain.Visibility = showMainEULA ? Visibility.Visible : Visibility.Collapsed;
                Eula3rdParty.Visibility = show3rdParty ? Visibility.Visible : Visibility.Collapsed;
                EulaMain.OKClick += (s, e) => AfterLoadFinishedAndEULA_Confirmed();
                Eula3rdParty.OKClick += (s, e) => AfterLoadFinishedAndEULA_Confirmed();
                AfterLoadFinishedAndEULA_Confirmed();
            }
        }

        private void AfterLoadFinishedAndEULA_Confirmed()
        {
            if (EulaMain.Visibility == Visibility.Visible) return;
            if (Eula3rdParty.Visibility == Visibility.Visible) return;
            // Re-enable managed controls
            IsEnabled = true;
            SetTabButtonsEnabled();
            if (BuildOptions.SHOW_TDP_SETTINGS)
            {
                var tdpWindow = new TDPSettingsWindow();
                tdpWindow.DataContext = _vm;
                tdpWindow.Show();
            }

            if (MinerPluginsManager.PluginsForEulaConfirm.Any() && !Launcher.IsUpdated)
            {
                var pluginsPopup = new Plugins.PluginsConfirmDialog();
                pluginsPopup.DataContext = new Plugins.PluginsConfirmDialog.VM
                {
                    Plugins = new ObservableCollection<PluginPackageInfoCR>(MinerPluginsManager.PluginsForEulaConfirm)
                };
                ShowContentAsModalDialog(pluginsPopup);
            }
            if (MinerPluginsManager.PluginsForEulaConfirm.Any() && Launcher.IsUpdated)
            {
                ConfigManager.InitDeviceSettings();
            }

            if (LoginSuccess.HasValue)
            {
                var description = LoginSuccess.Value ? Translations.Tr("Login performed successfully.") : Translations.Tr("Unable to retreive BTC address. Please retreive it by yourself from web page.");
                var btcLoginDialog = new CustomDialog()
                {
                    Title = Translations.Tr("Login"),
                    OkText = Translations.Tr("Ok"),
                    CancelVisible = Visibility.Collapsed,
                    AnimationVisible = Visibility.Collapsed,
                    Description = description
                };
                btcLoginDialog.OKClick += (s, e) =>
                {
                    if (!LoginSuccess.Value) Helpers.VisitUrlLink(Links.Login);
                };
                CustomDialogManager.ShowModalDialog(btcLoginDialog);
            }

            if (Launcher.IsUpdated)
            {
                var nhmUpdatedDialog = new CustomDialog()
                {
                    Title = Translations.Tr("NiceHash Miner Updated"),
                    Description = Translations.Tr("Completed NiceHash Miner auto update."),
                    OkText = Translations.Tr("OK"),
                    CancelVisible = Visibility.Collapsed,
                    AnimationVisible = Visibility.Collapsed
                };
                ShowContentAsModalDialog(nhmUpdatedDialog);
            }

            if (Launcher.IsUpdatedFailed)
            {
                var nhmUpdatedDialog = new CustomDialog()
                {
                    Title = Translations.Tr("NiceHash Miner Autoupdate Failed"),
                    Description = Translations.Tr("NiceHash Miner auto update failed to complete. Autoupdates are disabled until next miner launch."),
                    OkText = Translations.Tr("OK"),
                    CancelVisible = Visibility.Collapsed,
                    AnimationVisible = Visibility.Collapsed
                };
                ShowContentAsModalDialog(nhmUpdatedDialog);
            }
        }

        private async void MainWindow_OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            await MainWindow_OnClosingTask(e);
        }

        private async Task MainWindow_OnClosingTask(System.ComponentModel.CancelEventArgs e)
        {
            // Only ever try to prevent closing once
            if (_miningStoppedOnClose) return;

            _miningStoppedOnClose = true;
            //e.Cancel = true;
            IsEnabled = false;
            //await _vm.StopMining();
            await ApplicationStateManager.BeforeExit();
            taskbarIcon.Dispose();
            ApplicationStateManager.ExecuteApplicationExit();
            //Close();
        }
        #endregion Start-Loaded/Closing

        protected override void OnTabSelected(ToggleButtonType tabType)
        {
            var tabName = tabType.ToString();
            foreach (TabItem tab in MainTabs.Items)
            {
                if (tabName.Contains(tab.Name))
                {
                    MainTabs.SelectedItem = tab;
                    break;
                }
            }
        }

        #region Minimize to tray stuff
        private void CloseMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TaskbarIcon_OnTrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void MainWindow_OnStateChanged(object sender, EventArgs e)
        {
            if (!_vm.GUISettings.MinimizeToTray) return;
            if (WindowState == WindowState.Minimized) // TODO && config min to tray
                Hide();
        }
        #endregion Minimize to tray stuff

        private void StartAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() => _vm.StartMining());
        }

        private void StopAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() => _vm.StopMining());
        }


        private bool HasWriteAccessToFolder(string folderPath)
        {
            try
            {
                // Attempt to get a list of security permissions from the folder. 
                // This will raise an exception if the path is read only or do not have access to view the permissions. 
                var ds = new DirectoryInfo(folderPath).GetAccessControl();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private bool nhmwsDialogShown = false;
        private void CheckConnection(object sender, ElapsedEventArgs e)
        {
            if (!_vm.NHMWSConnected && !nhmwsDialogShown)
            {
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        var dialog = new CustomDialog
                        {
                            Title = Translations.Tr("NHMWS not connected"),
                            Description = Translations.Tr("Not connected to NHMWS. Please check your internet connection."),
                            OkText = Translations.Tr("Need help?"),
                            CancelVisible = Visibility.Collapsed,
                            OkVisible = Visibility.Visible,
                            AnimationVisible = Visibility.Collapsed,

                        };
                        dialog.OKClick += (s, e) =>
                        {
                            Helpers.VisitUrlLink(Links.NHMWSNotConnected);
                        };
                        CustomDialogManager.ShowModalDialog(dialog);
                    });
                    nhmwsDialogShown = true;
                    _timer.Stop();
                    _timer.Interval = 1000;
                    _timer.Start();
                }
                catch (Exception ex)
                {
                    Logger.Error("MainVM.IsNHMWSConnected", ex.Message);
                }
            }
            else if (_vm.NHMWSConnected && nhmwsDialogShown)
            {
                try
                {
                    Dispatcher.Invoke(() => CustomDialogManager.HideCurrentModal());
                    nhmwsDialogShown = false;
                    _timer.Stop();
                    _timer.Interval = 1000 * 60 * 2; //2min
                    _timer.Start();
                }
                catch (Exception ex)
                {
                    _timer.Stop();
                    Logger.Error("MainVM.IsNHMWSConnected", ex.Message);
                }
            }
        }
    }
}
