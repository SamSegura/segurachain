using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SeguraChain_Desktop_Wallet.Common;
using SeguraChain_Desktop_Wallet.Components;
using SeguraChain_Desktop_Wallet.Language.Enum;
using SeguraChain_Desktop_Wallet.Language.Object;
using SeguraChain_Lib.Blockchain.Setting;

namespace SeguraChain_Desktop_Wallet.InternalForm.Rescan
{
    public partial class ClassWalletRescanInternalForm : Form
    {
        private string _walletFilename;
        private ClassWalletRescanFormLanguage _walletRescanFormLanguageObject;
        private CancellationTokenSource _walletRescanCancellationToken;
        private double _walletRescanProgressPercent;
        private bool _walletEnableRescan;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="walletFilename"></param>
        /// <param name="walletEnableRescan"></param>
        public ClassWalletRescanInternalForm(string walletFilename, bool walletEnableRescan)
        {
            _walletFilename = walletFilename;
            _walletEnableRescan = walletEnableRescan;
            _walletRescanFormLanguageObject = ClassDesktopWalletCommonData.LanguageDatabase.GetLanguageContentObject<ClassWalletRescanFormLanguage>(ClassLanguageEnumType.LANGUAGE_TYPE_WALLET_RESCAN_FORM);
            _walletRescanCancellationToken = new CancellationTokenSource();
            InitializeComponent();
        }

        /// <summary>
        /// Enable every events of rescan after the load of this form.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ClassWalletRescanInternalForm_Load(object sender, EventArgs e)
        {
            Text = BlockchainSetting.CoinName + _walletRescanFormLanguageObject.FORM_TITLE_WALLET_RESCAN_FORM_TEXT;
            labelWalletRescanPending.Text = _walletFilename + _walletRescanFormLanguageObject.LABEL_WALLET_RESCAN_PENDING_TEXT;
            labelWalletRescanPending = ClassGraphicsUtility.AutoSetLocationAndResizeControl<Label>(labelWalletRescanPending, this, 50d, false);

            if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData.ContainsKey(_walletFilename))
            {
                TaskRescanWalletFile();
            }
            else
            {
                MessageBox.Show(_walletRescanFormLanguageObject.MESSAGEBOX_WALLET_RESCAN_ERROR_CONTENT_TEXT, _walletRescanFormLanguageObject.MESSAGEBOX_WALLET_RESCAN_ERROR_TITLE_TEXT, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        /// <summary>
        /// Task who await the end of rescan of the wallet file.
        /// </summary>
        private void TaskRescanWalletFile()
        {
            try
            {
                Task.Factory.StartNew(async () =>
                {
                    UpdateWalletRescanPercentProgress();

                    long lastBlockHeightSynced = await ClassDesktopWalletCommonData.WalletSyncSystem.GetLastBlockHeightUnlockedSynced(_walletRescanCancellationToken);

                    if (lastBlockHeightSynced >= BlockchainSetting.GenesisBlockHeight)
                    {
                        if (_walletEnableRescan)
                        {
                            ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[_walletFilename].WalletEnableRescan = true;
                            ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[_walletFilename].WalletBalanceCalculated = false;
                            while (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[_walletFilename].WalletEnableRescan)
                            {
                                await Task.Delay(1000, _walletRescanCancellationToken.Token);
                            }
                        }

                        while (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[_walletFilename].WalletLastBlockHeightSynced < (lastBlockHeightSynced - 1) || !ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[_walletFilename].WalletBalanceCalculated)
                        {
                            // Force to stop.
                            if (!ClassDesktopWalletCommonData.DesktopWalletStarted)
                            {
                                break;
                            }

                            if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[_walletFilename].WalletLastBlockHeightSynced > 0)
                            {
                                _walletRescanProgressPercent = ((double)ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[_walletFilename].WalletLastBlockHeightSynced / lastBlockHeightSynced) * 100d;
                            }

                            UpdateWalletRescanPercentProgress();

                            await Task.Delay(10, _walletRescanCancellationToken.Token);
                        }

                        #region Just for indicate the final progress if it's too fast.
                    
                        if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[_walletFilename].WalletLastBlockHeightSynced > 0)
                        {
                            _walletRescanProgressPercent = ((double)ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[_walletFilename].WalletLastBlockHeightSynced / lastBlockHeightSynced) * 100d;
                        }

                        #endregion

                    }
                    // No data synced.
                    else
                    {
                        _walletRescanProgressPercent = 100;
                    }

                    UpdateWalletRescanPercentProgress();
                    await Task.Delay(1000);
                    BeginInvoke((MethodInvoker)Close);

                }, _walletRescanCancellationToken.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Ignored, catch the exception once the task is cancelled.
            }
        }

        /// <summary>
        /// Update the rescan process bar.
        /// </summary>
        private void UpdateWalletRescanPercentProgress()
        {
            MethodInvoker invoke = () =>
            {
                labelWalletRescanProgressText.Text = _walletRescanProgressPercent.ToString("N" + 2) + _walletRescanFormLanguageObject.LABEL_WALLET_RESCAN_PROGRESS_TEXT;
                labelWalletRescanProgressText = ClassGraphicsUtility.AutoSetLocationAndResizeControl<Label>(labelWalletRescanProgressText, this, 50d, false);
                int percentProgress = (int)_walletRescanProgressPercent;
                if (percentProgress <= progressBarProgressRescan.Maximum)
                {
                    progressBarProgressRescan.Value = percentProgress;
                }
                else
                {
                    progressBarProgressRescan.Value = progressBarProgressRescan.Maximum;
                }
            };

            BeginInvoke(invoke);
        }

        /// <summary>
        /// Executed once the form is closed, cancel the task who await the rescan of the wallet file.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ClassWalletRescanInternalForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                if (_walletRescanCancellationToken != null)
                {
                    if (!_walletRescanCancellationToken.IsCancellationRequested)
                    {
                        _walletRescanCancellationToken.Cancel();
                    }
                }
            }
            catch
            {
                // Ignored, attempt to cancel the task who await the end of rescan.
            }
        }
    }
}
