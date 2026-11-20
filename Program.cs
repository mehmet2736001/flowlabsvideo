using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FlowVideoStudio
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { File.WriteAllText("crash.log", e.ExceptionObject.ToString()); } catch { }
            };
            Application.ThreadException += (s, e) =>
            {
                try { File.WriteAllText("crash.log", e.Exception.ToString()); } catch { }
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainStudioForm());
            }
            catch (Exception ex)
            {
                try { File.WriteAllText("crash.log", ex.ToString()); } catch { }
            }
        }
    }

    public class StudioConfig
    {
        public string flowUrl { get; set; }
        public string ttsUrl { get; set; }
        public string customTtsUrl { get; set; }
        public string ttsInputSelector { get; set; }
        public string ttsButtonSelector { get; set; }
        public bool autoPipeline { get; set; }
        public int expectedVideoCount { get; set; }
        public string resolution { get; set; }
        public int fps { get; set; }
        public string activeProject { get; set; }

        public StudioConfig()
        {
            flowUrl = "https://flow.google.com/project/d98f8df7-c977-46ba-8dd8-f39f24714571/tool/1af05941-50b5-4c91-b5d1-d4df10f10273";
            ttsUrl = "https://aiseslendirme.asliortabakan40.workers.dev/";
            customTtsUrl = "https://aiseslendirme.asliortabakan40.workers.dev/";
            ttsInputSelector = "#json-timeline-textarea, textarea";
            ttsButtonSelector = "button";
            autoPipeline = true;
            expectedVideoCount = 3;
            resolution = "1080x1920";
            fps = 30;
            activeProject = "DefaultProject";
        }
    }

    public class MainStudioForm : Form
    {
        private readonly string appBaseDir;
        private readonly string uiDir;
        private readonly string userDataDir;
        private readonly string configPath;
        private readonly string userDownloadsDir;
        private string projectDir;
        private string videosDir;
        private string audioDir;
        private string rendersDir;
        private string scriptMdPath;

        private StudioConfig config;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private CoreWebView2Environment webViewEnv;
        private FileSystemWatcher downloadsWatcher;

        // UI Controls
        private Panel topMasterBar;
        private Button btnTabFlow;
        private Button btnTabTts;
        private Button btnTabEditor;
        private Button btnAutoPipeline;
        private Button btnSyncDownloads;
        private Button btnCaptureFlowNow;
        private Button btnRunScriptTts;
        private Button btnOpenFolder;

        private Panel browserNavBar;
        private Button btnBack;
        private Button btnForward;
        private Button btnReload;
        private Button btnHome;
        private TextBox txtUrl;
        private Button btnGoUrl;
        private Button btnTtsHub;

        private Panel webContainer;
        private WebView2 wvFlow;
        private WebView2 wvTts;
        private WebView2 wvEditor;

        private Panel bottomStatusBar;
        private Label lblStatus;
        private Label lblCounts;

        private int activeTabIndex = 1; // 1 = Flow, 2 = TTS, 3 = CapCut Editor
        private bool isTtsAutomationRunning = false;

        public MainStudioForm()
        {
            appBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            uiDir = Path.Combine(appBaseDir, "ui");
            userDataDir = Path.Combine(appBaseDir, "UserData");
            configPath = Path.Combine(appBaseDir, "config.json");
            userDownloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            LoadConfig();
            EnsureProjectDirectories();
            InitializeUi();
            SetupDownloadsFolderWatcher();
            this.Load += async (s, e) => await InitializeWebViewsAsync();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    config = json.Deserialize<StudioConfig>(File.ReadAllText(configPath, Encoding.UTF8)) ?? new StudioConfig();
                }
                else
                {
                    config = new StudioConfig();
                    SaveConfig();
                }
            }
            catch
            {
                config = new StudioConfig();
            }
        }

        private void SaveConfig()
        {
            try
            {
                File.WriteAllText(configPath, json.Serialize(config), Encoding.UTF8);
            }
            catch { }
        }

        private void EnsureProjectDirectories()
        {
            projectDir = Path.Combine(appBaseDir, "Projects", string.IsNullOrEmpty(config.activeProject) ? "DefaultProject" : config.activeProject);
            videosDir = Path.Combine(projectDir, "videos");
            audioDir = Path.Combine(projectDir, "audio");
            rendersDir = Path.Combine(projectDir, "renders");
            scriptMdPath = Path.Combine(projectDir, "script.md");

            Directory.CreateDirectory(uiDir);
            Directory.CreateDirectory(userDataDir);
            Directory.CreateDirectory(videosDir);
            Directory.CreateDirectory(audioDir);
            Directory.CreateDirectory(rendersDir);

            // Auto-import script.md and video1..N.mp4 from Downloads if available on startup
            ImportFromDownloadsFolder(false);
        }

        // =========================================================================
        // DOWNLOADS FOLDER WATCHER (AUTO-DETECTS script.md & video1.mp4..videoN.mp4)
        // =========================================================================
        private void SetupDownloadsFolderWatcher()
        {
            try
            {
                if (!Directory.Exists(userDownloadsDir)) return;
                downloadsWatcher = new FileSystemWatcher(userDownloadsDir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler onChange = async (s, e) =>
                {
                    string name = (e.Name ?? "").ToLowerInvariant();
                    if (name == "script.md" || Regex.IsMatch(name, @"^video\d+\.(mp4|webm|mov)$"))
                    {
                        await Task.Delay(1200);
                        this.BeginInvoke(new Action(async () =>
                        {
                            bool changed = ImportFromDownloadsFolder(true);
                            if (changed && config.autoPipeline)
                            {
                                await CheckAutoPipelineAfterVideoAsync();
                            }
                        }));
                    }
                };
                downloadsWatcher.Created += onChange;
                downloadsWatcher.Changed += onChange;
                downloadsWatcher.Renamed += (s, e) => onChange(s, e);
            }
            catch { }
        }

        private bool ImportFromDownloadsFolder(bool notifyUi)
        {
            bool anyCopied = false;
            try
            {
                if (!Directory.Exists(userDownloadsDir)) return false;

                string dlScript = Path.Combine(userDownloadsDir, "script.md");
                if (File.Exists(dlScript))
                {
                    File.Copy(dlScript, scriptMdPath, true);
                    anyCopied = true;
                }

                var dlVideos = Directory.GetFiles(userDownloadsDir)
                    .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^video\d+\.(mp4|webm|mov)$", RegexOptions.IgnoreCase))
                    .OrderBy(f => ExtractNumberFromName(Path.GetFileName(f)))
                    .ToList();

                foreach (string vf in dlVideos)
                {
                    string fn = Path.GetFileName(vf);
                    string dest = Path.Combine(videosDir, fn);
                    FileInfo srcInfo = new FileInfo(vf);
                    FileInfo dstInfo = new FileInfo(dest);
                    if (!dstInfo.Exists || dstInfo.Length != srcInfo.Length || srcInfo.LastWriteTimeUtc > dstInfo.LastWriteTimeUtc)
                    {
                        File.Copy(vf, dest, true);
                        anyCopied = true;
                    }
                }

                if (notifyUi)
                {
                    UpdateStatusBarCounts();
                    PushProjectStateToEditor(true);
                    SetStatus(string.Format("📥 Downloads klasöründen script.md ve {0} adet Flow videosu (video1..{0}) projeye aktarıldı!", dlVideos.Count));
                }
            }
            catch (Exception ex)
            {
                if (notifyUi) SetStatus("Downloads aktarım hatası: " + ex.Message);
            }
            return anyCopied;
        }

        private int ExtractNumberFromName(string fileName)
        {
            Match m = Regex.Match(fileName ?? "", @"(\d+)");
            int val;
            if (m.Success && int.TryParse(m.Groups[1].Value, out val)) return val;
            return 9999;
        }

        private void InitializeUi()
        {
            this.Text = "FlowVideoStudio - Google Flow Otomasyon, Seslendirme & CapCut Video Kurgu Stüdyosu";
            this.Size = new Size(1540, 930);
            this.MinimumSize = new Size(1100, 720);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.WindowState = FormWindowState.Maximized;
            this.BackColor = Color.FromArgb(10, 12, 16);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // 1. TOP MASTER TAB & AUTOMATION BAR
            topMasterBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Color.FromArgb(17, 22, 31),
                Padding = new Padding(10, 8, 10, 8)
            };

            btnTabFlow = CreateBarButton("🎬 1. Google Flow", 170, Color.FromArgb(0, 150, 199));
            btnTabFlow.Location = new Point(10, 8);
            btnTabFlow.Click += (s, e) => SwitchTab(1);

            btnTabTts = CreateBarButton("🎙️ 2. Seslendirme Sitesi", 190, Color.FromArgb(26, 33, 48));
            btnTabTts.Location = new Point(186, 8);
            btnTabTts.Click += (s, e) => SwitchTab(2);

            btnTabEditor = CreateBarButton("✂️ 3. CapCut Kurgu & Render", 220, Color.FromArgb(26, 33, 48));
            btnTabEditor.Location = new Point(382, 8);
            btnTabEditor.Click += (s, e) => SwitchTab(3);

            // Right-aligned automation controls
            FlowLayoutPanel rightActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent
            };

            btnAutoPipeline = CreateBarButton(
                config.autoPipeline ? "⚡ Tam Otomasyon: AÇIK" : "⚡ Tam Otomasyon: KAPALI",
                170,
                config.autoPipeline ? Color.FromArgb(0, 150, 100) : Color.FromArgb(45, 52, 65));
            btnAutoPipeline.Click += (s, e) =>
            {
                config.autoPipeline = !config.autoPipeline;
                SaveConfig();
                btnAutoPipeline.Text = config.autoPipeline ? "⚡ Tam Otomasyon: AÇIK" : "⚡ Tam Otomasyon: KAPALI";
                btnAutoPipeline.BackColor = config.autoPipeline ? Color.FromArgb(0, 150, 100) : Color.FromArgb(45, 52, 65);
                SetStatus(config.autoPipeline
                    ? "Tam Otomasyon AÇIK: Downloads/Flow videoları tamamlandığında script.md otomatik seslendirilip Timeline'a aktarılacak."
                    : "Tam Otomasyon KAPALI: Adımları üstteki butonlarla manuel tetikleyebilirsiniz.");
            };

            btnSyncDownloads = CreateBarButton("📥 Downloads (video1..N + script)", 215, Color.FromArgb(0, 120, 160));
            btnSyncDownloads.Click += (s, e) =>
            {
                ImportFromDownloadsFolder(true);
                SwitchTab(3);
            };

            btnRunScriptTts = CreateBarButton("🔊 JSON Yapıştır & Seslendir", 185, Color.FromArgb(180, 100, 10));
            btnRunScriptTts.Click += async (s, e) => await TriggerScriptTtsPipelineAsync();

            Button btnUploadAudioAdvance = CreateBarButton("🎵 Sesi Yükle & Editöre Geç ➔", 195, Color.FromArgb(0, 140, 80));
            btnUploadAudioAdvance.Click += (s, e) =>
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = "VoxAtelier'den İndirdiğiniz Ses Dosyasını (.wav / .mp3) Seçin";
                    ofd.InitialDirectory = Directory.Exists(userDownloadsDir) ? userDownloadsDir : projectDir;
                    ofd.Filter = "Ses Dosyaları (*.wav;*.mp3;*.m4a;*.ogg)|*.wav;*.mp3;*.m4a;*.ogg|Tüm Dosyalar (*.*)|*.*";
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        string destFile = Path.Combine(audioDir, Path.GetFileName(ofd.FileName));
                        File.Copy(ofd.FileName, destFile, true);
                        UpdateStatusBarCounts();
                        SwitchTab(3);
                        PushProjectStateToEditor(true);
                        SetStatus("✅ Ses dosyası yüklendi ve videolarla birlikte CapCut Video Edit arayüzüne aktarıldı!");
                    }
                }
            };

            btnOpenFolder = CreateBarButton("📂 Klasör", 90, Color.FromArgb(35, 43, 58));
            btnOpenFolder.Click += (s, e) => Process.Start("explorer.exe", projectDir);

            rightActions.Controls.Add(btnAutoPipeline);
            rightActions.Controls.Add(btnSyncDownloads);
            rightActions.Controls.Add(btnRunScriptTts);
            rightActions.Controls.Add(btnUploadAudioAdvance);
            rightActions.Controls.Add(btnOpenFolder);

            topMasterBar.Controls.Add(btnTabFlow);
            topMasterBar.Controls.Add(btnTabTts);
            topMasterBar.Controls.Add(btnTabEditor);
            topMasterBar.Controls.Add(rightActions);

            // 2. SECONDARY BROWSER NAVIGATION BAR
            browserNavBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.FromArgb(24, 30, 42),
                Padding = new Padding(10, 5, 10, 5)
            };

            btnBack = CreateSmallNavButton("◀", 34);
            btnBack.Location = new Point(10, 5);
            btnBack.Click += (s, e) => { var wv = GetActiveBrowserWebView(); if (wv != null && wv.CoreWebView2 != null && wv.CanGoBack) wv.GoBack(); };

            btnForward = CreateSmallNavButton("▶", 34);
            btnForward.Location = new Point(48, 5);
            btnForward.Click += (s, e) => { var wv = GetActiveBrowserWebView(); if (wv != null && wv.CoreWebView2 != null && wv.CanGoForward) wv.GoForward(); };

            btnReload = CreateSmallNavButton("🔄", 36);
            btnReload.Location = new Point(86, 5);
            btnReload.Click += (s, e) => { var wv = GetActiveBrowserWebView(); if (wv != null && wv.CoreWebView2 != null) wv.Reload(); };

            btnHome = CreateSmallNavButton("🏠 Ana Sayfa", 96);
            btnHome.Location = new Point(126, 5);
            btnHome.Click += (s, e) =>
            {
                if (activeTabIndex == 1 && wvFlow != null && wvFlow.CoreWebView2 != null)
                    wvFlow.CoreWebView2.Navigate(config.flowUrl);
                else if (activeTabIndex == 2 && wvTts != null && wvTts.CoreWebView2 != null)
                    wvTts.CoreWebView2.Navigate(!string.IsNullOrWhiteSpace(config.customTtsUrl) ? config.customTtsUrl : config.ttsUrl);
            };

            btnTtsHub = CreateSmallNavButton("🛠️ TTS Köprü Ayarları", 145);
            btnTtsHub.BackColor = Color.FromArgb(65, 35, 110);
            btnTtsHub.Visible = false;

            btnGoUrl = CreateSmallNavButton("🌐 Git & Kaydet", 115);
            btnGoUrl.BackColor = Color.FromArgb(0, 120, 160);
            btnGoUrl.Click += (s, e) => NavigateUrlFromBar();

            txtUrl = new TextBox
            {
                BackColor = Color.FromArgb(12, 16, 23),
                ForeColor = Color.FromArgb(220, 235, 255),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 10f),
                Location = new Point(228, 7),
                Height = 26
            };
            txtUrl.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    NavigateUrlFromBar();
                }
            };

            btnTtsHub.Click += (s, e) =>
            {
                if (wvTts != null && wvTts.CoreWebView2 != null)
                    wvTts.CoreWebView2.Navigate("https://app.studio/tts_hub.html");
            };

            browserNavBar.Controls.Add(btnBack);
            browserNavBar.Controls.Add(btnForward);
            browserNavBar.Controls.Add(btnReload);
            browserNavBar.Controls.Add(btnHome);
            browserNavBar.Controls.Add(txtUrl);
            browserNavBar.Controls.Add(btnGoUrl);
            browserNavBar.Controls.Add(btnTtsHub);

            browserNavBar.Resize += (s, e) => LayoutBrowserNavBar();

            // 3. BOTTOM STATUS BAR
            bottomStatusBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                BackColor = Color.FromArgb(14, 18, 26),
                Padding = new Padding(12, 4, 12, 4)
            };

            lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Hazır. Downloads klasöründeki script.md ve video1..N otomatik izleniyor.",
                ForeColor = Color.FromArgb(0, 229, 255),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };

            lblCounts = new Label
            {
                Dock = DockStyle.Right,
                Width = 420,
                Text = "Videolar: 0 | Sesler: 0 | FFmpeg: Hazır",
                ForeColor = Color.FromArgb(160, 175, 195),
                TextAlign = ContentAlignment.MiddleRight
            };

            bottomStatusBar.Controls.Add(lblStatus);
            bottomStatusBar.Controls.Add(lblCounts);

            // 4. MAIN WEBVIEW CONTAINER
            webContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(10, 12, 16)
            };

            wvFlow = new WebView2 { Dock = DockStyle.Fill, Visible = true };
            wvTts = new WebView2 { Dock = DockStyle.Fill, Visible = false };
            wvEditor = new WebView2 { Dock = DockStyle.Fill, Visible = false };

            webContainer.Controls.Add(wvFlow);
            webContainer.Controls.Add(wvTts);
            webContainer.Controls.Add(wvEditor);

            this.Controls.Add(webContainer);
            this.Controls.Add(browserNavBar);
            this.Controls.Add(topMasterBar);
            this.Controls.Add(bottomStatusBar);

            LayoutBrowserNavBar();
            UpdateStatusBarCounts();
        }

        private void LayoutBrowserNavBar()
        {
            int rightReserve = activeTabIndex == 2 ? 275 : 130;
            int urlWidth = Math.Max(200, browserNavBar.ClientSize.Width - 238 - rightReserve);
            txtUrl.Width = urlWidth;
            btnGoUrl.Location = new Point(txtUrl.Right + 6, 5);
            btnTtsHub.Location = new Point(btnGoUrl.Right + 6, 5);
        }

        private Button CreateBarButton(string text, int width, Color bg)
        {
            var btn = new Button
            {
                Text = text,
                Width = width,
                Height = 35,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.75f, FontStyle.Bold)
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = Color.FromArgb(55, 68, 90);
            return btn;
        }

        private Button CreateSmallNavButton(string text, int width)
        {
            var btn = new Button
            {
                Text = text,
                Width = width,
                Height = 29,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(34, 42, 58),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = Color.FromArgb(55, 68, 90);
            return btn;
        }

        private WebView2 GetActiveBrowserWebView()
        {
            if (activeTabIndex == 1) return wvFlow;
            if (activeTabIndex == 2) return wvTts;
            return wvEditor;
        }

        private void SwitchTab(int tabIndex)
        {
            activeTabIndex = tabIndex;
            wvFlow.Visible = (tabIndex == 1);
            wvTts.Visible = (tabIndex == 2);
            wvEditor.Visible = (tabIndex == 3);

            btnTabFlow.BackColor = (tabIndex == 1) ? Color.FromArgb(0, 150, 199) : Color.FromArgb(26, 33, 48);
            btnTabTts.BackColor = (tabIndex == 2) ? Color.FromArgb(124, 77, 255) : Color.FromArgb(26, 33, 48);
            btnTabEditor.BackColor = (tabIndex == 3) ? Color.FromArgb(255, 59, 92) : Color.FromArgb(26, 33, 48);

            browserNavBar.Visible = (tabIndex == 1 || tabIndex == 2);
            btnTtsHub.Visible = (tabIndex == 2);
            LayoutBrowserNavBar();

            if (tabIndex == 1 && wvFlow != null && wvFlow.CoreWebView2 != null)
            {
                txtUrl.Text = wvFlow.Source != null ? wvFlow.Source.ToString() : config.flowUrl;
            }
            else if (tabIndex == 2 && wvTts != null && wvTts.CoreWebView2 != null)
            {
                txtUrl.Text = wvTts.Source != null ? wvTts.Source.ToString() : config.ttsUrl;
            }
            else if (tabIndex == 3)
            {
                PushProjectStateToEditor(false);
            }
        }

        private void NavigateUrlFromBar()
        {
            string raw = txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(raw)) return;
            if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                raw = "https://" + raw;
            }

            if (activeTabIndex == 1 && wvFlow != null && wvFlow.CoreWebView2 != null)
            {
                config.flowUrl = raw;
                SaveConfig();
                wvFlow.CoreWebView2.Navigate(raw);
            }
            else if (activeTabIndex == 2 && wvTts != null && wvTts.CoreWebView2 != null)
            {
                if (!raw.Contains("app.studio/tts_hub.html"))
                {
                    config.customTtsUrl = raw;
                    SaveConfig();
                }
                wvTts.CoreWebView2.Navigate(raw);
                SetStatus("Seslendirme sitesi kaydedildi ve açılıyor: " + raw);
            }
        }

        // =========================================================================
        // WEBVIEW2 INITIALIZATION & ANTI-DETECTION / OAUTH POPUP SUPPORT
        // =========================================================================
        private async Task InitializeWebViewsAsync()
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions(
                    "--disable-blink-features=AutomationControlled --autoplay-policy=no-user-gesture-required"
                );
                webViewEnv = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);

                await wvFlow.EnsureCoreWebView2Async(webViewEnv);
                await wvTts.EnsureCoreWebView2Async(webViewEnv);
                await wvEditor.EnsureCoreWebView2Async(webViewEnv);

                ConfigureCommonWebView(wvFlow, "flow");
                ConfigureCommonWebView(wvTts, "tts");
                ConfigureCommonWebView(wvEditor, "editor");

                await wvFlow.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetFlowInjectionScript());
                wvFlow.NavigationCompleted += (s, e) =>
                {
                    if (activeTabIndex == 1) txtUrl.Text = wvFlow.Source != null ? wvFlow.Source.ToString() : "";
                };
                wvFlow.CoreWebView2.WebMessageReceived += OnFlowWebMessageReceived;

                await wvTts.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetTtsInjectionScript());
                wvTts.NavigationCompleted += async (s, e) =>
                {
                    if (activeTabIndex == 2) txtUrl.Text = wvTts.Source != null ? wvTts.Source.ToString() : "";
                    if (isTtsAutomationRunning)
                    {
                        await Task.Delay(1200);
                        await InjectScriptTextIntoTtsPageAsync();
                    }
                };
                wvTts.CoreWebView2.WebMessageReceived += OnTtsWebMessageReceived;

                wvEditor.CoreWebView2.WebMessageReceived += OnEditorWebMessageReceived;

                wvFlow.CoreWebView2.Navigate(config.flowUrl);
                string initialTtsUrl = !string.IsNullOrWhiteSpace(config.customTtsUrl) ? config.customTtsUrl : config.ttsUrl;
                wvTts.CoreWebView2.Navigate(initialTtsUrl);
                wvEditor.CoreWebView2.Navigate("https://app.studio/editor.html");

                txtUrl.Text = config.flowUrl;
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView2 başlatılırken hata oluştu:\n" + ex.Message, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ConfigureCommonWebView(WebView2 wv, string role)
        {
            var core = wv.CoreWebView2;
            core.Settings.IsScriptEnabled = true;
            core.Settings.AreDefaultScriptDialogsEnabled = true;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDevToolsEnabled = true;

            string ua = core.Settings.UserAgent ?? "";
            if (ua.Contains("WebView2"))
            {
                core.Settings.UserAgent = ua.Replace("WebView2", "").Trim();
            }

            core.SetVirtualHostNameToFolderMapping("app.studio", uiDir, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("media.studio", projectDir, CoreWebView2HostResourceAccessKind.Allow);

            core.NewWindowRequested += async (s, e) =>
            {
                var deferral = e.GetDeferral();
                try
                {
                    Form popupForm = new Form
                    {
                        Text = "Google Oturum Açma / Pencere",
                        Size = new Size(920, 720),
                        StartPosition = FormStartPosition.CenterParent,
                        BackColor = Color.FromArgb(14, 18, 26)
                    };
                    WebView2 popupWv = new WebView2 { Dock = DockStyle.Fill };
                    popupForm.Controls.Add(popupWv);
                    popupForm.Show(this);

                    await popupWv.EnsureCoreWebView2Async(webViewEnv);
                    string popUa = popupWv.CoreWebView2.Settings.UserAgent ?? "";
                    if (popUa.Contains("WebView2"))
                        popupWv.CoreWebView2.Settings.UserAgent = popUa.Replace("WebView2", "").Trim();

                    popupWv.CoreWebView2.WindowCloseRequested += (ws, we) => popupForm.Close();
                    e.NewWindow = popupWv.CoreWebView2;
                    e.Handled = true;
                }
                catch { }
                finally
                {
                    deferral.Complete();
                }
            };

            core.DownloadStarting += (s, e) => HandleMediaDownload(role, e);
        }

        // =========================================================================
        // SILENT DOWNLOAD INTERCEPTOR (NAMES FLOW VIDEOS video1.mp4, video2.mp4...)
        // =========================================================================
        private void HandleMediaDownload(string sourceRole, CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                string origName = Path.GetFileName(e.ResultFilePath ?? "download.bin");
                string ext = Path.GetExtension(origName).ToLowerInvariant();
                string mime = (e.DownloadOperation.MimeType ?? "").ToLowerInvariant();

                // If Flow downloads script.md directly
                if (origName.Equals("script.md", StringComparison.OrdinalIgnoreCase) || ext == ".md")
                {
                    e.ResultFilePath = scriptMdPath;
                    e.Handled = true;
                    e.DownloadOperation.StateChanged += (ds, de) =>
                    {
                        if (e.DownloadOperation.State == CoreWebView2DownloadState.Completed)
                        {
                            UpdateStatusBarCounts();
                            PushProjectStateToEditor(true);
                            SetStatus("📜 script.md indirildi ve sahneler ayrıştırıldı!");
                        }
                    };
                    return;
                }

                bool isAudio = sourceRole == "tts" ||
                               mime.StartsWith("audio/") ||
                               ext == ".mp3" || ext == ".wav" || ext == ".ogg" || ext == ".m4a" || ext == ".aac";

                string targetPath;
                string fileName;
                string mediaType;

                if (isAudio)
                {
                    mediaType = "audio";
                    if (string.IsNullOrEmpty(ext) || (ext != ".mp3" && ext != ".wav" && ext != ".ogg" && ext != ".m4a"))
                        ext = mime.Contains("wav") ? ".wav" : ".mp3";
                    int nextIdx = Directory.GetFiles(audioDir).Length + 1;
                    fileName = string.Format("seslendirme_{0}{1}", nextIdx, ext);
                    targetPath = Path.Combine(audioDir, fileName);
                }
                else
                {
                    mediaType = "video";
                    if (string.IsNullOrEmpty(ext) || (ext != ".mp4" && ext != ".webm" && ext != ".mov"))
                        ext = ".mp4";

                    // Keep original name if it is already video1.mp4, video2.mp4, etc.
                    if (Regex.IsMatch(origName, @"^video\d+\.(mp4|webm|mov)$", RegexOptions.IgnoreCase))
                    {
                        fileName = origName;
                    }
                    else
                    {
                        int nextIdx = Directory.GetFiles(videosDir).Length + 1;
                        fileName = string.Format("video{0}{1}", nextIdx, ext);
                    }
                    targetPath = Path.Combine(videosDir, fileName);
                }

                e.ResultFilePath = targetPath;
                e.Handled = true;

                SetStatus(string.Format("İndiriliyor ({0}): {1}...", mediaType == "video" ? "Flow Video" : "Seslendirme", fileName));

                e.DownloadOperation.StateChanged += async (ds, de) =>
                {
                    if (e.DownloadOperation.State == CoreWebView2DownloadState.Completed)
                    {
                        double dur = ProbeMediaDuration(targetPath, mediaType == "video" ? 10.0 : 10.0);
                        SetStatus(string.Format("✅ {0} yakalandı ve projeye eklendi: {1} ({2:0.0} sn)",
                            mediaType == "video" ? "Flow Videosu" : "Seslendirme", fileName, dur));

                        UpdateStatusBarCounts();
                        NotifyEditorNewMedia(mediaType, fileName, targetPath, dur);

                        if (mediaType == "video")
                        {
                            await CheckAutoPipelineAfterVideoAsync();
                        }
                        else if (mediaType == "audio" && isTtsAutomationRunning)
                        {
                            isTtsAutomationRunning = false;
                            SwitchTab(3);
                            PushProjectStateToEditor(true);
                            SetStatus("🎉 Tüm videolar ve script.md seslendirmesi CapCut Timeline'ına aktarıldı! Render almaya hazır.");
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                SetStatus("İndirme yakalama hatası: " + ex.Message);
            }
        }

        private async Task CheckAutoPipelineAfterVideoAsync()
        {
            if (!config.autoPipeline) return;

            int capturedVideos = GetSortedProjectVideoFiles().Count;
            int expected = Math.Max(1, GetExpectedSceneCountFromScript());
            if (capturedVideos >= expected && !isTtsAutomationRunning)
            {
                SetStatus(string.Format("⚡ Hedeflenen {0}/{1} Flow videosu (video1..{0}) tamamlandı! Otomatik olarak script.md seslendirme aşamasına geçiliyor...", capturedVideos, expected));
                await Task.Delay(800);
                await TriggerScriptTtsPipelineAsync();
            }
        }

        // =========================================================================
        // JAVASCRIPT INJECTION BRIDGES FOR GOOGLE FLOW & USER'S TTS WEBSITE
        // =========================================================================
        private string GetFlowInjectionScript()
        {
            return @"
(function() {
  try {
    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
  } catch (e) {}

  window.__capturedFlowSrcs = window.__capturedFlowSrcs || new Set();

  async function downloadFlowVideoSrc(src) {
    if (!src) return false;
    try {
      const resp = await fetch(src);
      const blob = await resp.blob();
      if (blob.size < 10000) return false;
      const blobUrl = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = blobUrl;
      a.download = 'flow_scene.mp4';
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(blobUrl), 10000);
      return true;
    } catch (err) {
      const a = document.createElement('a');
      a.href = src;
      a.download = 'flow_scene.mp4';
      document.body.appendChild(a);
      a.click();
      a.remove();
      return true;
    }
  }

  window.__scanAndCaptureFlowVideos = async function(onlyNew) {
    const videos = Array.from(document.querySelectorAll('video'));
    let count = 0;
    for (const v of videos) {
      const src = v.currentSrc || v.src || (v.querySelector('source') && v.querySelector('source').src);
      if (!src) continue;
      if (onlyNew && window.__capturedFlowSrcs.has(src)) continue;
      if (v.readyState >= 1 && (v.duration > 0.5 || !isNaN(v.duration))) {
        window.__capturedFlowSrcs.add(src);
        const ok = await downloadFlowVideoSrc(src);
        if (ok) count++;
      }
    }
    return count;
  };

  window.__injectFlowPrompt = function(promptText) {
    const el = document.querySelector('textarea, [contenteditable=""true""], input[type=""text""]');
    if (!el) return false;
    el.focus();
    if (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') {
      const setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value')?.set
                  || Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
      if (setter) setter.call(el, promptText);
      else el.value = promptText;
    } else {
      el.textContent = promptText;
    }
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  };

  function decorateFlowVideos() {
    const videos = document.querySelectorAll('video');
    videos.forEach(v => {
      if (v.dataset.flowStudioDecorated) return;
      v.dataset.flowStudioDecorated = '1';

      const parent = v.parentElement;
      if (parent && getComputedStyle(parent).position === 'static') {
        parent.style.position = 'relative';
      }

      const btn = document.createElement('button');
      btn.textContent = '🎬 Studio\'ya Aktar';
      btn.style.cssText = 'position:absolute; top:10px; right:10px; z-index:99999; background:linear-gradient(135deg,#00b4d8,#7c4dff); color:#fff; border:none; padding:6px 12px; border-radius:6px; font-weight:700; font-size:12px; cursor:pointer; box-shadow:0 4px 12px rgba(0,0,0,0.6);';
      btn.onclick = async (e) => {
        e.stopPropagation();
        e.preventDefault();
        const src = v.currentSrc || v.src || (v.querySelector('source') && v.querySelector('source').src);
        if (src) {
          btn.textContent = '⏳ Aktarılıyor...';
          window.__capturedFlowSrcs.add(src);
          await downloadFlowVideoSrc(src);
          btn.textContent = '✅ Studio\'ya Aktarıldı';
          btn.style.background = '#00c853';
        }
      };
      if (parent) parent.appendChild(btn);

      v.addEventListener('loadeddata', () => {
        const src = v.currentSrc || v.src;
        if (src && !window.__capturedFlowSrcs.has(src) && v.duration > 0.8) {
          if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({
              type: 'flow_video_detected',
              src: src,
              duration: v.duration || 10
            }));
          }
        }
      });
    });
  }

  setInterval(decorateFlowVideos, 1800);
})();";
        }

        private string GetTtsInjectionScript()
        {
            return @"
(function() {
  window.__capturedAudioSrcs = window.__capturedAudioSrcs || new Set();

  async function downloadTtsAudioSrc(src) {
    if (!src || window.__capturedAudioSrcs.has(src)) return;
    window.__capturedAudioSrcs.add(src);
    try {
      const resp = await fetch(src);
      const blob = await resp.blob();
      if (blob.size < 1000) return;
      const ext = (blob.type && blob.type.includes('wav')) ? '.wav' : '.mp3';
      const blobUrl = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = blobUrl;
      a.download = 'tts_output' + ext;
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(blobUrl), 10000);
    } catch (e) {
      const a = document.createElement('a');
      a.href = src;
      a.download = 'tts_output.mp3';
      document.body.appendChild(a);
      a.click();
      a.remove();
    }
  }

  window.__fillAndRunExternalTts = function(voxJsonText, scenes, inputSelector, btnSelector, narratorTone) {
    // 1. Check if VoxAtelier ('JSON Olarak Yapıştır' tab) is present on the page
    const allButtons = Array.from(document.querySelectorAll('button'));
    const jsonTabBtn = allButtons.find(b => /JSON Olarak Yapıştır/i.test(b.textContent || ''));
    if (jsonTabBtn) {
      jsonTabBtn.click();
    }

    setTimeout(() => {
      const jsonArea = document.querySelector('#json-timeline-textarea')
                    || document.querySelector(inputSelector || 'textarea');
      if (jsonArea) {
        jsonArea.focus();
        const proto = jsonArea.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
        const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
        if (setter) setter.call(jsonArea, voxJsonText);
        else jsonArea.value = voxJsonText;
        jsonArea.dispatchEvent(new Event('input', { bubbles: true }));
        jsonArea.dispatchEvent(new Event('change', { bubbles: true }));
      }

      // Click 'JSON Zaman Çizelgesine Göre Full Sesi Üret' button using the currently selected voice
      setTimeout(() => {
        const btnsNow = Array.from(document.querySelectorAll('button'));
        let genBtn = btnsNow.find(b => /JSON Zaman Çizelgesine Göre|Full Sesi Üret|Sahneleri Seslendir/i.test(b.textContent || ''));
        if (!genBtn) {
          genBtn = btnsNow.find(b => /Metni Seslendir|Seslendir|Üret|Generate/i.test(b.textContent || '') && !/JSON Olarak/i.test(b.textContent || ''));
        }
        if (genBtn && !genBtn.disabled) {
          genBtn.click();
        }
      }, 450);
    }, 350);

    return 'voxatelier_json_injected';
  };

  setInterval(() => {
    if (location.hostname === 'app.studio') return;
    const audios = document.querySelectorAll('audio');
    audios.forEach(a => {
      const src = a.currentSrc || a.src || (a.querySelector('source') && a.querySelector('source').src);
      if (src && !window.__capturedAudioSrcs.has(src)) {
        downloadTtsAudioSrc(src);
      }
    });
  }, 1500);
})();";
        }

        // =========================================================================
        // WEB MESSAGE HANDLERS (FLOW, TTS, CAPCUT EDITOR)
        // =========================================================================
        private async void OnFlowWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;
                var msg = json.Deserialize<Dictionary<string, object>>(raw);
                string type = msg.ContainsKey("type") ? Convert.ToString(msg["type"]) : "";

                if (type == "flow_video_detected" && config.autoPipeline)
                {
                    await Task.Delay(1200);
                    await wvFlow.CoreWebView2.ExecuteScriptAsync("window.__scanAndCaptureFlowVideos && window.__scanAndCaptureFlowVideos(true);");
                }
            }
            catch { }
        }

        private async void OnTtsWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;
                var msg = json.Deserialize<Dictionary<string, object>>(raw);
                string type = msg.ContainsKey("type") ? Convert.ToString(msg["type"]) : "";

                if (type == "set_custom_tts_url")
                {
                    string url = Convert.ToString(msg["url"]).Trim();
                    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        url = "https://" + url;

                    config.customTtsUrl = url;
                    if (msg.ContainsKey("inputSelector")) config.ttsInputSelector = Convert.ToString(msg["inputSelector"]);
                    if (msg.ContainsKey("buttonSelector")) config.ttsButtonSelector = Convert.ToString(msg["buttonSelector"]);
                    SaveConfig();

                    txtUrl.Text = url;
                    wvTts.CoreWebView2.Navigate(url);
                    SetStatus("Kendi seslendirme siteniz kaydedildi ve açılıyor: " + url);
                }
                else if (type == "save_tts_selectors")
                {
                    if (msg.ContainsKey("inputSelector")) config.ttsInputSelector = Convert.ToString(msg["inputSelector"]);
                    if (msg.ContainsKey("buttonSelector")) config.ttsButtonSelector = Convert.ToString(msg["buttonSelector"]);
                    SaveConfig();
                    SetStatus("Seslendirme sitesi CSS seçicileri güncellendi.");
                }
                else if (type == "tts_hub_ready")
                {
                    string scriptText = File.Exists(scriptMdPath) ? File.ReadAllText(scriptMdPath, Encoding.UTF8) : "";
                    string cleanNarration = ExtractCleanFullNarration(scriptText);
                    var scenes = ParseScriptMdScenes(scriptText);
                    var initPayload = new Dictionary<string, object>
                    {
                        { "type", "FLOW_STUDIO_SCRIPT" },
                        { "text", cleanNarration },
                        { "scenes", scenes },
                        { "autoExecute", false }
                    };
                    await wvTts.CoreWebView2.ExecuteScriptAsync(
                        string.Format("window.postMessage({0}, '*');", json.Serialize(initPayload)));
                }
                else if (type == "synthesize_local_tts")
                {
                    string text = msg.ContainsKey("text") ? Convert.ToString(msg["text"]) : "";
                    string mode = msg.ContainsKey("mode") ? Convert.ToString(msg["mode"]) : "scenes_separate";
                    await Task.Run(() => SynthesizeLocalWavFiles(text, mode));
                }
            }
            catch (Exception ex)
            {
                SetStatus("TTS mesaj hatası: " + ex.Message);
            }
        }

        private async void OnEditorWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;
                var msg = json.Deserialize<Dictionary<string, object>>(raw);
                string type = msg.ContainsKey("type") ? Convert.ToString(msg["type"]) : "";

                if (type == "editor_ready" || type == "refresh_project")
                {
                    PushProjectStateToEditor(false);
                }
                else if (type == "import_from_downloads")
                {
                    ImportFromDownloadsFolder(true);
                }
                else if (type == "save_script_md")
                {
                    string content = msg.ContainsKey("content") ? Convert.ToString(msg["content"]) : "";
                    File.WriteAllText(scriptMdPath, content, Encoding.UTF8);
                    if (msg.ContainsKey("expectedScenes"))
                    {
                        config.expectedVideoCount = Math.Max(1, Convert.ToInt32(msg["expectedScenes"]));
                        SaveConfig();
                    }
                    UpdateStatusBarCounts();
                    SetStatus("script.md kaydedildi (" + config.expectedVideoCount + " klip).");
                }
                else if (type == "load_script_dialog")
                {
                    using (OpenFileDialog ofd = new OpenFileDialog())
                    {
                        ofd.InitialDirectory = Directory.Exists(userDownloadsDir) ? userDownloadsDir : projectDir;
                        ofd.Filter = "Markdown / Metin Dosyaları (*.md;*.txt)|*.md;*.txt|Tüm Dosyalar (*.*)|*.*";
                        if (ofd.ShowDialog(this) == DialogResult.OK)
                        {
                            string content = File.ReadAllText(ofd.FileName, Encoding.UTF8);
                            File.WriteAllText(scriptMdPath, content, Encoding.UTF8);
                            UpdateStatusBarCounts();
                            PushProjectStateToEditor(true);
                            SetStatus("script.md yüklendi: " + ofd.FileName);
                        }
                    }
                }
                else if (type == "import_media_dialog")
                {
                    using (OpenFileDialog ofd = new OpenFileDialog())
                    {
                        ofd.InitialDirectory = Directory.Exists(userDownloadsDir) ? userDownloadsDir : projectDir;
                        ofd.Multiselect = true;
                        ofd.Filter = "Medya Dosyaları (*.mp4;*.webm;*.mov;*.mp3;*.wav;*.m4a;*.ogg)|*.mp4;*.webm;*.mov;*.mp3;*.wav;*.m4a;*.ogg";
                        if (ofd.ShowDialog(this) == DialogResult.OK)
                        {
                            foreach (string srcFile in ofd.FileNames)
                            {
                                string ext = Path.GetExtension(srcFile).ToLowerInvariant();
                                bool isAud = ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".ogg";
                                string destFolder = isAud ? audioDir : videosDir;
                                string destFile = Path.Combine(destFolder, Path.GetFileName(srcFile));
                                File.Copy(srcFile, destFile, true);
                            }
                            UpdateStatusBarCounts();
                            PushProjectStateToEditor(true);
                            SetStatus("Seçilen medya dosyaları projeye ve Timeline'a eklendi.");
                        }
                    }
                }
                else if (type == "open_project_folder")
                {
                    Process.Start("explorer.exe", projectDir);
                }
                else if (type == "open_renders_folder")
                {
                    Process.Start("explorer.exe", rendersDir);
                }
                else if (type == "open_rendered_file")
                {
                    string path = msg.ContainsKey("path") ? Convert.ToString(msg["path"]) : "";
                    if (File.Exists(path)) Process.Start(path);
                }
                else if (type == "send_prompt_to_flow")
                {
                    string prompt = msg.ContainsKey("prompt") ? Convert.ToString(msg["prompt"]) : "";
                    SwitchTab(1);
                    if (wvFlow != null && wvFlow.CoreWebView2 != null)
                    {
                        Clipboard.SetText(prompt);
                        await wvFlow.CoreWebView2.ExecuteScriptAsync(
                            string.Format("window.__injectFlowPrompt && window.__injectFlowPrompt({0});", json.Serialize(prompt)));
                        SetStatus("Sahne İngilizce Video Promptu Google Flow'a gönderildi (ve panoya kopyalandı).");
                    }
                }
                else if (type == "run_tts_for_script")
                {
                    await TriggerScriptTtsPipelineAsync();
                }
                else if (type == "start_ffmpeg_render")
                {
                    await RunFfmpegTimelineRenderAsync(msg);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Editör mesaj hatası: " + ex.Message);
            }
        }

        // =========================================================================
        // EXACT FLOW LABS SCRIPT.MD PARSER IN C#
        // =========================================================================
        private string ExtractCleanFullNarration(string mdText)
        {
            if (string.IsNullOrWhiteSpace(mdText)) return "";

            // 1. Check "## 📜 Tam Türkçe Seslendirme Metni (Script)" section
            Match mFull = Regex.Match(mdText, @"^##\s+.*Tam Türkçe Seslendirme Metni.*$\r?\n([\s\S]*?)(?=^##\s+|\z)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (mFull.Success)
            {
                string block = mFull.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(block)) return block;
            }

            // 2. Fallback: join all "- **Türkçe Dış Ses:**" lines from scenes
            var scenes = ParseScriptMdScenes(mdText);
            string joined = string.Join(" ", scenes.Select(s => s["narration"]).Where(n => !string.IsNullOrWhiteSpace(n)));
            return !string.IsNullOrWhiteSpace(joined) ? joined : mdText;
        }

        private string ExtractNarratorTone(string mdText)
        {
            if (string.IsNullOrWhiteSpace(mdText)) return "";
            Match mTone = Regex.Match(mdText, @"^##\s+.*Anlatıcı Yönergesi.*$\r?\n([\s\S]*?)(?=^##\s+|\z)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return mTone.Success ? mTone.Groups[1].Value.Trim() : "";
        }

        private List<Dictionary<string, string>> ParseScriptMdScenes(string mdText)
        {
            var list = new List<Dictionary<string, string>>();
            if (string.IsNullOrWhiteSpace(mdText)) return list;

            // Match ### Sahne X (00:00–00:10) blocks
            MatchCollection matches = Regex.Matches(
                mdText,
                @"^###\s+(Sahne\s*\d+[^\r\n]*)\r?\n([\s\S]*?)(?=^###\s+|^##\s+|\z)",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            foreach (Match m in matches)
            {
                string header = m.Groups[1].Value.Trim();
                string body = m.Groups[2].Value;

                Match mTitle = Regex.Match(body, @"\*\*Başlık:\*\*\s*(.+)", RegexOptions.IgnoreCase);
                Match mVoice = Regex.Match(body, @"\*\*(?:Türkçe Dış Ses|Seslendirme|Dış Ses):\*\*\s*(.+)", RegexOptions.IgnoreCase);
                Match mPromptCode = Regex.Match(body, @"```[a-z]*\s*([\s\S]*?)```", RegexOptions.IgnoreCase);

                string title = mTitle.Success ? mTitle.Groups[1].Value.Trim() : header;
                string narration = mVoice.Success ? mVoice.Groups[1].Value.Trim() : "";
                narration = narration.Trim('"', '“', '”', '\'', ' ');
                string prompt = mPromptCode.Success ? mPromptCode.Groups[1].Value.Trim() : "";

                list.Add(new Dictionary<string, string>
                {
                    { "header", header },
                    { "title", title },
                    { "narration", narration },
                    { "prompt", prompt }
                });
            }

            return list;
        }

        private int GetExpectedSceneCountFromScript()
        {
            if (!File.Exists(scriptMdPath)) return Math.Max(1, config.expectedVideoCount);
            string md = File.ReadAllText(scriptMdPath, Encoding.UTF8);

            Match mKlip = Regex.Match(md, @"\((\d+)\s*Klip\)", RegexOptions.IgnoreCase);
            int klipCount;
            if (mKlip.Success && int.TryParse(mKlip.Groups[1].Value, out klipCount) && klipCount > 0)
                return klipCount;

            var scenes = ParseScriptMdScenes(md);
            return scenes.Count > 0 ? scenes.Count : Math.Max(1, config.expectedVideoCount);
        }

        private async Task TriggerManualFlowScanAsync()
        {
            if (wvFlow == null || wvFlow.CoreWebView2 == null) return;
            SetStatus("Google Flow sayfasındaki videolar taranıyor ve indiriliyor...");
            await wvFlow.CoreWebView2.ExecuteScriptAsync(
                "window.__scanAndCaptureFlowVideos ? window.__scanAndCaptureFlowVideos(false) : 0;");
        }

        private string BuildVoxAtelierJsonFromScriptMd(string mdText)
        {
            string text = (mdText ?? "").Trim();
            if (text.StartsWith("{")) return text;

            Match mTitle = Regex.Match(text, @"^#\s+(.+)$", RegexOptions.Multiline);
            string title = mTitle.Success ? mTitle.Groups[1].Value.Trim() : "Flow Video Projesi";

            Match mFormat = Regex.Match(text, @"\*\*Format:\*\*\s*([0-9:]+)", RegexOptions.IgnoreCase);
            string aspectRatio = mFormat.Success ? mFormat.Groups[1].Value.Trim() : "9:16";

            string fullNarration = ExtractCleanFullNarration(text);
            string toneRaw = ExtractNarratorTone(text).Replace("\r", " ").Replace("\n", " ").Trim();

            string voiceType = "Derin, epik, tok ve gizemli bir erkek sesi";
            string pacing = !string.IsNullOrEmpty(toneRaw) ? toneRaw : "Her sahnedeki metni sahne süresine yayarak, gerilim dolu ve dramatik bir ritimle oku.";
            int dotIdx = toneRaw.IndexOf('.');
            if (dotIdx > 0)
            {
                voiceType = toneRaw.Substring(0, dotIdx).Trim();
                string rest = toneRaw.Substring(dotIdx + 1).Trim();
                if (!string.IsNullOrEmpty(rest)) pacing = rest;
            }

            var clips = new List<Dictionary<string, object>>();
            MatchCollection matches = Regex.Matches(
                text,
                @"^###\s+(Sahne\s*\d+[^\r\n]*)\r?\n([\s\S]*?)(?=^###\s+|^##\s+|\z)",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            int idx = 0;
            int maxEndSec = 0;
            foreach (Match m in matches)
            {
                string header = m.Groups[1].Value.Trim();
                string body = m.Groups[2].Value;

                int startSec = idx * 10;
                int endSec = (idx + 1) * 10;
                string timeRange = string.Format("{0:D2}:{1:D2}–{2:D2}:{3:D2}", startSec / 60, startSec % 60, endSec / 60, endSec % 60);

                Match tm = Regex.Match(header, @"\((\d+):(\d+)\s*[–\-]\s*(\d+):(\d+)\)");
                if (tm.Success)
                {
                    startSec = int.Parse(tm.Groups[1].Value) * 60 + int.Parse(tm.Groups[2].Value);
                    endSec = int.Parse(tm.Groups[3].Value) * 60 + int.Parse(tm.Groups[4].Value);
                    timeRange = string.Format("{0}:{1}–{2}:{3}", tm.Groups[1].Value, tm.Groups[2].Value, tm.Groups[3].Value, tm.Groups[4].Value);
                }
                if (endSec > maxEndSec) maxEndSec = endSec;

                Match mSceneTitle = Regex.Match(body, @"\*\*Başlık:\*\*\s*(.+)", RegexOptions.IgnoreCase);
                Match mVisual = Regex.Match(body, @"\*\*Görsel Özet:\*\*\s*(.+)", RegexOptions.IgnoreCase);
                Match mVoice = Regex.Match(body, @"\*\*(?:Türkçe Dış Ses|Seslendirme|Dış Ses):\*\*\s*(.+)", RegexOptions.IgnoreCase);

                string sceneTitle = mSceneTitle.Success ? mSceneTitle.Groups[1].Value.Trim() : ("Sahne " + (idx + 1));
                string visualDesc = mVisual.Success ? mVisual.Groups[1].Value.Trim() : "";
                string narration = mVoice.Success ? mVoice.Groups[1].Value.Trim().Trim('"', '“', '”', '\'', ' ') : "";

                clips.Add(new Dictionary<string, object>
                {
                    { "clip_number", idx + 1 },
                    { "time_range", timeRange },
                    { "start_seconds", startSec },
                    { "end_seconds", endSec },
                    { "duration_seconds", Math.Max(1, endSec - startSec) },
                    { "scene_title", sceneTitle },
                    { "visual_description", visualDesc },
                    { "narration_text", narration }
                });
                idx++;
            }

            if (maxEndSec == 0) maxEndSec = Math.Max(10, clips.Count * 10);

            var root = new Dictionary<string, object>
            {
                { "project", new Dictionary<string, object>
                    {
                        { "title", title },
                        { "language", "tr" },
                        { "aspect_ratio", aspectRatio },
                        { "total_duration_seconds", maxEndSec },
                        { "clip_count", Math.Max(1, clips.Count) }
                    }
                },
                { "narration_direction", new Dictionary<string, object>
                    {
                        { "voice_type", voiceType },
                        { "style", "Klasik tarihi belgesel anlatıcısı tonu" },
                        { "pacing", pacing }
                    }
                },
                { "full_narrator_script", fullNarration },
                { "clips", clips }
            };

            return json.Serialize(root);
        }

        private async Task TriggerScriptTtsPipelineAsync()
        {
            isTtsAutomationRunning = true;
            SwitchTab(2);
            SetStatus("script.md VoxAtelier JSON formatına dönüştürülüyor ve seçili ses ile yapıştırılıyor...");
            await Task.Delay(350);
            await InjectScriptTextIntoTtsPageAsync();
        }

        private async Task InjectScriptTextIntoTtsPageAsync()
        {
            if (wvTts == null || wvTts.CoreWebView2 == null) return;

            string scriptText = File.Exists(scriptMdPath) ? File.ReadAllText(scriptMdPath, Encoding.UTF8) : "";
            string voxJsonString = BuildVoxAtelierJsonFromScriptMd(scriptText);
            try { Clipboard.SetText(voxJsonString); } catch { }

            string narratorTone = ExtractNarratorTone(scriptText);
            var scenes = ParseScriptMdScenes(scriptText);

            string jsCall = string.Format(
                "window.__fillAndRunExternalTts && window.__fillAndRunExternalTts({0}, {1}, {2}, {3}, {4});",
                json.Serialize(voxJsonString),
                json.Serialize(scenes),
                json.Serialize(config.ttsInputSelector),
                json.Serialize(config.ttsButtonSelector),
                json.Serialize(narratorTone)
            );
            await wvTts.CoreWebView2.ExecuteScriptAsync(jsCall);
            SetStatus("✅ VoxAtelier JSON oluşturuldu, 'JSON Olarak Yapıştır' kutusuna yapıştırıldı ve seçili ses ile seslendirme başlatıldı!");
        }

        // =========================================================================
        // BUILT-IN LOCAL WAV VOICEOVER SYNTHESIZER
        // =========================================================================
        private void SynthesizeLocalWavFiles(string fullText, string mode)
        {
            try
            {
                string scriptText = File.Exists(scriptMdPath) ? File.ReadAllText(scriptMdPath, Encoding.UTF8) : fullText;
                var scenes = ParseScriptMdScenes(scriptText);
                string cleanFull = ExtractCleanFullNarration(scriptText);
                if (string.IsNullOrWhiteSpace(cleanFull)) cleanFull = fullText;

                string lastFileName = "";

                if (mode == "scenes_separate" && scenes.Count > 1)
                {
                    for (int i = 0; i < scenes.Count; i++)
                    {
                        string nar = scenes[i]["narration"];
                        if (string.IsNullOrWhiteSpace(nar)) continue;

                        string fName = string.Format("ses_video{0}.wav", i + 1);
                        string outPath = Path.Combine(audioDir, fName);
                        GenerateSpeechWavFile(nar, outPath);
                        lastFileName = fName;
                    }
                }
                else
                {
                    string fName = "seslendirme_tam.wav";
                    string outPath = Path.Combine(audioDir, fName);
                    GenerateSpeechWavFile(cleanFull, outPath);
                    lastFileName = fName;
                }

                this.BeginInvoke(new Action(() =>
                {
                    UpdateStatusBarCounts();
                    string previewUrl = "https://media.studio/audio/" + lastFileName;
                    if (wvTts != null && wvTts.CoreWebView2 != null)
                    {
                        wvTts.CoreWebView2.ExecuteScriptAsync(
                            string.Format("window.onLocalTtsCompleted && window.onLocalTtsCompleted({0}, {1});",
                            json.Serialize(previewUrl), json.Serialize(lastFileName)));
                    }

                    PushProjectStateToEditor(true);
                    if (isTtsAutomationRunning || config.autoPipeline)
                    {
                        isTtsAutomationRunning = false;
                        SwitchTab(3);
                        SetStatus("🎉 script.md seslendirildi ve video1..N ile CapCut Timeline'ına otomatik dizildi!");
                    }
                    else
                    {
                        SetStatus("✅ Seslendirme tamamlandı: " + lastFileName);
                    }
                }));
            }
            catch (Exception ex)
            {
                this.BeginInvoke(new Action(() => SetStatus("Yerel TTS hatası: " + ex.Message)));
            }
        }

        private void GenerateSpeechWavFile(string text, string outputPath)
        {
            try
            {
                using (SpeechSynthesizer synth = new SpeechSynthesizer())
                {
                    foreach (var v in synth.GetInstalledVoices())
                    {
                        if (v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                        {
                            synth.SelectVoice(v.VoiceInfo.Name);
                            break;
                        }
                    }
                    synth.SetOutputToWaveFile(outputPath);
                    synth.Speak(text);
                    synth.SetOutputToNull();
                }
                if (new FileInfo(outputPath).Length > 200) return;
            }
            catch { }

            WriteProceduralToneWav(outputPath, 9.5);
        }

        private void WriteProceduralToneWav(string path, double durationSec)
        {
            int sampleRate = 44100;
            int totalSamples = (int)(sampleRate * durationSec);
            using (FileStream fs = new FileStream(path, FileMode.Create))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                int byteRate = sampleRate * 2;
                int dataSize = totalSamples * 2;
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataSize);
                bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                bw.Write(16);
                bw.Write((short)1);
                bw.Write((short)1);
                bw.Write(sampleRate);
                bw.Write(byteRate);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataSize);

                for (int i = 0; i < totalSamples; i++)
                {
                    double t = (double)i / sampleRate;
                    double env = Math.Sin(Math.PI * Math.Min(1.0, (t % 0.45) / 0.45));
                    double sample = 0.25 * env * (Math.Sin(2 * Math.PI * 190 * t) + 0.5 * Math.Sin(2 * Math.PI * 380 * t));
                    short val = (short)(sample * short.MaxValue);
                    bw.Write(val);
                }
            }
        }

        // =========================================================================
        // EDITOR STATE SYNC & NATURAL NUMERICAL SORT (video1, video2, video3)
        // =========================================================================
        private List<string> GetSortedProjectVideoFiles()
        {
            if (!Directory.Exists(videosDir)) return new List<string>();
            return Directory.GetFiles(videosDir)
                .Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => ExtractNumberFromName(Path.GetFileName(f)))
                .ThenBy(f => Path.GetFileName(f))
                .ToList();
        }

        private void PushProjectStateToEditor(bool autoAssemble)
        {
            if (wvEditor == null || wvEditor.CoreWebView2 == null) return;

            var videos = GetSortedProjectVideoFiles()
                .Select(f =>
                {
                    string fn = Path.GetFileName(f);
                    return new Dictionary<string, object>
                    {
                        { "name", fn },
                        { "fileName", fn },
                        { "url", "https://media.studio/videos/" + Uri.EscapeDataString(fn) },
                        { "localPath", f },
                        { "duration", ProbeMediaDuration(f, 10.0) }
                    };
                }).ToList();

            var audios = Directory.GetFiles(audioDir)
                .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => ExtractNumberFromName(Path.GetFileName(f)))
                .ThenBy(f => Path.GetFileName(f))
                .Select(f =>
                {
                    string fn = Path.GetFileName(f);
                    return new Dictionary<string, object>
                    {
                        { "name", fn },
                        { "fileName", fn },
                        { "url", "https://media.studio/audio/" + Uri.EscapeDataString(fn) },
                        { "localPath", f },
                        { "duration", ProbeMediaDuration(f, 10.0) }
                    };
                }).ToList();

            string scriptMd = File.Exists(scriptMdPath) ? File.ReadAllText(scriptMdPath, Encoding.UTF8) : "";

            var state = new Dictionary<string, object>
            {
                { "scriptMd", scriptMd },
                { "videos", videos },
                { "audios", audios },
                { "autoAssemble", autoAssemble }
            };

            wvEditor.CoreWebView2.ExecuteScriptAsync(
                string.Format("window.onHostProjectState && window.onHostProjectState({0});", json.Serialize(state)));
        }

        private void NotifyEditorNewMedia(string mediaType, string fileName, string localPath, double duration)
        {
            if (wvEditor == null || wvEditor.CoreWebView2 == null) return;
            string folder = mediaType == "video" ? "videos" : "audio";
            var item = new Dictionary<string, object>
            {
                { "mediaType", mediaType },
                { "name", fileName },
                { "fileName", fileName },
                { "url", string.Format("https://media.studio/{0}/{1}", folder, Uri.EscapeDataString(fileName)) },
                { "localPath", localPath },
                { "duration", duration }
            };
            wvEditor.CoreWebView2.ExecuteScriptAsync(
                string.Format("window.onHostNewMediaCaptured && window.onHostNewMediaCaptured({0});", json.Serialize(item)));
        }

        private double ProbeMediaDuration(string filePath, double fallbackSec)
        {
            try
            {
                if (!File.Exists(filePath)) return fallbackSec;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = string.Format("-i \"{0}\"", filePath),
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(3000);
                    Match m = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                    if (m.Success)
                    {
                        double h = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        double min = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        double sec = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                        double total = h * 3600 + min * 60 + sec;
                        if (total > 0.1) return Math.Round(total, 2);
                    }
                }
            }
            catch { }
            return fallbackSec;
        }

        private bool ProbeHasAudio(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = string.Format("-i \"{0}\"", filePath),
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(3000);
                    return Regex.IsMatch(err, @"Stream #\d+:\d+.*Audio:", RegexOptions.IgnoreCase);
                }
            }
            catch { }
            return false;
        }

        // =========================================================================
        // FFMPEG MULTI-TRACK CAPCUT TIMELINE RENDER ENGINE
        // =========================================================================
        private async Task RunFfmpegTimelineRenderAsync(Dictionary<string, object> payload)
        {
            string res = payload.ContainsKey("resolution") ? Convert.ToString(payload["resolution"]) : "1080x1920";
            int fps = payload.ContainsKey("fps") ? Convert.ToInt32(payload["fps"]) : 30;
            bool burnSubs = payload.ContainsKey("burnSubtitles") && Convert.ToBoolean(payload["burnSubtitles"]);

            string[] resParts = res.Split('x');
            int targetW = resParts.Length == 2 ? int.Parse(resParts[0]) : 1080;
            int targetH = resParts.Length == 2 ? int.Parse(resParts[1]) : 1920;

            var tl = payload["timeline"] as Dictionary<string, object>;
            var videoClips = ToDictList(tl != null && tl.ContainsKey("videoClips") ? tl["videoClips"] : null)
                .OrderBy(c => GetDouble(c, "start", 0)).ToList();
            var pipClips = ToDictList(tl != null && tl.ContainsKey("pipClips") ? tl["pipClips"] : null)
                .OrderBy(c => GetDouble(c, "start", 0)).ToList();
            var audioClips = ToDictList(tl != null && tl.ContainsKey("audioClips") ? tl["audioClips"] : null)
                .OrderBy(c => GetDouble(c, "start", 0)).ToList();
            var bgmClips = ToDictList(tl != null && tl.ContainsKey("bgmClips") ? tl["bgmClips"] : null)
                .OrderBy(c => GetDouble(c, "start", 0)).ToList();
            var subClips = ToDictList(tl != null && tl.ContainsKey("subClips") ? tl["subClips"] : null)
                .OrderBy(c => GetDouble(c, "start", 0)).ToList();

            if (videoClips.Count == 0)
            {
                SendRenderUpdate(100, "Hata: Timeline'da video klibi yok.", "", false, "");
                return;
            }

            string outFileName = string.Format("final_render_{0:yyyyMMdd_HHmmss}.mp4", DateTime.Now);
            string outputPath = Path.Combine(rendersDir, outFileName);

            SendRenderUpdate(12, "FFmpeg filtre grafiği ve timeline kurgusu hazırlanıyor...", "Çıktı: " + outputPath, false, "");

            await Task.Run(() =>
            {
                try
                {
                    StringBuilder inputs = new StringBuilder();
                    StringBuilder filter = new StringBuilder();

                    int inputIdx = 0;
                    StringBuilder concatVideoLabels = new StringBuilder();
                    List<string> mixAudioLabels = new List<string>();

                    for (int i = 0; i < videoClips.Count; i++)
                    {
                        var vc = videoClips[i];
                        string localPath = Convert.ToString(vc["localPath"]);
                        if (!File.Exists(localPath))
                            localPath = Path.Combine(videosDir, Convert.ToString(vc["fileName"]));

                        double trimIn = GetDouble(vc, "trimIn", 0);
                        double dur = Math.Max(0.2, GetDouble(vc, "duration", 10.0));
                        double speed = Math.Max(0.25, Math.Min(3.0, GetDouble(vc, "speed", 1.0)));
                        double srcDurNeeded = dur * speed;
                        double startSec = Math.Max(0, GetDouble(vc, "start", 0));
                        double vol = Math.Max(0, GetDouble(vc, "volume", 100)) / 100.0;

                        inputs.AppendFormat(CultureInfo.InvariantCulture, "-ss {0:0.###} -t {1:0.###} -i \"{2}\" ", trimIn, srcDurNeeded, localPath);

                        double ptsFactor = 1.0 / speed;
                        string vLabel = string.Format("[v{0}]", i);
                        filter.AppendFormat(CultureInfo.InvariantCulture,
                            "[{0}:v]setpts={1:0.####}*PTS,scale={2}:{3}:force_original_aspect_ratio=decrease,pad={2}:{3}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps={4},format=yuv420p{5};",
                            inputIdx, ptsFactor, targetW, targetH, fps, vLabel);

                        concatVideoLabels.Append(vLabel);

                        // Mix audio from this video clip if present
                        if (File.Exists(localPath) && ProbeHasAudio(localPath))
                        {
                            string vaLabel = string.Format("[va{0}]", i);
                            double audioSpeed = Math.Max(0.5, Math.Min(2.0, speed));
                            int delayMs = (int)Math.Round(startSec * 1000);
                            filter.AppendFormat(CultureInfo.InvariantCulture,
                                "[{0}:a]atempo={1:0.###},volume={2:0.##},adelay={3}|{3},aresample=44100{4};",
                                inputIdx, audioSpeed, vol, delayMs, vaLabel);
                            mixAudioLabels.Add(vaLabel);
                        }

                        inputIdx++;
                    }

                    string videoStageLabel = "[vconcat]";
                    filter.AppendFormat("{0}concat=n={1}:v=1:a=0{2};", concatVideoLabels.ToString(), videoClips.Count, videoStageLabel);

                    string srtPath = Path.Combine(projectDir, "temp_subs.srt");
                    if (burnSubs && subClips.Count > 0)
                    {
                        WriteSrtFile(srtPath, subClips);
                        filter.AppendFormat("{0}subtitles=temp_subs.srt:force_style='FontSize=16,PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000,BorderStyle=3,Outline=2,Shadow=0,MarginV=35'[vout];", videoStageLabel);
                        videoStageLabel = "[vout]";
                    }

                    string currentVideoStage = videoStageLabel;
                    for (int p = 0; p < pipClips.Count; p++)
                    {
                        var pc = pipClips[p];
                        string localPath = Convert.ToString(pc["localPath"]);
                        if (!File.Exists(localPath))
                            localPath = Path.Combine(videosDir, Convert.ToString(pc["fileName"]));
                        if (!File.Exists(localPath))
                            localPath = Path.Combine(projectDir, Convert.ToString(pc["fileName"]));
                        if (!File.Exists(localPath)) continue;

                        double pipStart = Math.Max(0, GetDouble(pc, "start", 0));
                        double pipDur = Math.Max(0.2, GetDouble(pc, "duration", 10.0));
                        double speed = Math.Max(0.25, Math.Min(3.0, GetDouble(pc, "speed", 1.0)));
                        bool loop = !pc.ContainsKey("loop") || Convert.ToBoolean(pc["loop"]);
                        bool chroma = !pc.ContainsKey("chromaKey") || Convert.ToBoolean(pc["chromaKey"]);
                        string pos = pc.ContainsKey("position") ? Convert.ToString(pc["position"]) : "bottom-center";
                        double scalePct = Math.Max(15, Math.Min(90, GetDouble(pc, "scalePercent", 38)));
                        int targetPipW = (int)Math.Round(targetW * (scalePct / 100.0));

                        if (loop)
                        {
                            inputs.AppendFormat(CultureInfo.InvariantCulture, "-stream_loop -1 -i \"{0}\" ", localPath);
                        }
                        else
                        {
                            double trimIn = Math.Max(0, GetDouble(pc, "trimIn", 0));
                            inputs.AppendFormat(CultureInfo.InvariantCulture, "-ss {0:0.###} -t {1:0.###} -i \"{2}\" ", trimIn, pipDur * speed, localPath);
                        }

                        string pipPrepLabel = string.Format("[pip_prep_{0}]", p);
                        if (chroma)
                        {
                            filter.AppendFormat(CultureInfo.InvariantCulture,
                                "[{0}:v]colorkey=0x13C322:0.25:0.05,scale={1}:-1,format=rgba{2};",
                                inputIdx, targetPipW, pipPrepLabel);
                        }
                        else
                        {
                            filter.AppendFormat(CultureInfo.InvariantCulture,
                                "[{0}:v]scale={1}:-1,format=rgba{2};",
                                inputIdx, targetPipW, pipPrepLabel);
                        }

                        string overlayPos;
                        int yOffset = (int)Math.Round(targetH * 0.08);
                        if (pos == "bottom-right") overlayPos = string.Format(CultureInfo.InvariantCulture, "W-w-30:H-h-{0}", yOffset);
                        else if (pos == "bottom-left") overlayPos = string.Format(CultureInfo.InvariantCulture, "30:H-h-{0}", yOffset);
                        else if (pos == "top-right") overlayPos = "W-w-30:50";
                        else if (pos == "top-left") overlayPos = "30:50";
                        else if (pos == "center") overlayPos = "(W-w)/2:(H-h)/2";
                        else overlayPos = string.Format(CultureInfo.InvariantCulture, "(W-w)/2:H-h-{0}", yOffset); // bottom-center logo

                        string nextVideoStage = string.Format("[vpip_{0}]", p);
                        filter.AppendFormat(CultureInfo.InvariantCulture,
                            "{0}{1}overlay={2}:enable='between(t,{3:0.###},{4:0.###})'{5};",
                            currentVideoStage, pipPrepLabel, overlayPos, pipStart, pipStart + pipDur, nextVideoStage);

                        currentVideoStage = nextVideoStage;
                        inputIdx++;
                    }
                    videoStageLabel = currentVideoStage;

                    var allAudioClips = new List<Dictionary<string, object>>();
                    allAudioClips.AddRange(audioClips);
                    allAudioClips.AddRange(bgmClips);

                    for (int i = 0; i < allAudioClips.Count; i++)
                    {
                        var ac = allAudioClips[i];
                        string localPath = Convert.ToString(ac["localPath"]);
                        if (!File.Exists(localPath))
                            localPath = Path.Combine(audioDir, Convert.ToString(ac["fileName"]));
                        if (!File.Exists(localPath)) continue;

                        double startSec = Math.Max(0, GetDouble(ac, "start", 0));
                        double trimIn = Math.Max(0, GetDouble(ac, "trimIn", 0));
                        double dur = Math.Max(0.2, GetDouble(ac, "duration", 10.0));
                        double speed = Math.Max(0.5, Math.Min(2.0, GetDouble(ac, "speed", 1.0)));
                        double vol = Math.Max(0, GetDouble(ac, "volume", 100)) / 100.0;
                        int delayMs = (int)Math.Round(startSec * 1000);

                        inputs.AppendFormat(CultureInfo.InvariantCulture, "-ss {0:0.###} -t {1:0.###} -i \"{2}\" ", trimIn, dur * speed, localPath);

                        string aLabel = string.Format("[a{0}]", i);
                        filter.AppendFormat(CultureInfo.InvariantCulture,
                            "[{0}:a]atempo={1:0.###},volume={2:0.##},adelay={3}|{3},aresample=44100{4};",
                            inputIdx, speed, vol, delayMs, aLabel);

                        mixAudioLabels.Add(aLabel);
                        inputIdx++;
                    }

                    string mapAudioArgs;
                    if (mixAudioLabels.Count > 0)
                    {
                        filter.AppendFormat("{0}amix=inputs={1}:duration=longest:dropout_transition=0:normalize=0[aout]",
                            string.Join("", mixAudioLabels), mixAudioLabels.Count);
                        mapAudioArgs = "-map \"[aout]\" -c:a aac -b:a 192k";
                    }
                    else
                    {
                        if (filter.Length > 0 && filter[filter.Length - 1] == ';')
                            filter.Length -= 1;
                        mapAudioArgs = "-an";
                    }

                    string fullArgs = string.Format(
                        "-y {0}-filter_complex \"{1}\" -map \"{2}\" {3} -c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p -movflags +faststart \"{4}\"",
                        inputs.ToString(),
                        filter.ToString().TrimEnd(';'),
                        videoStageLabel,
                        mapAudioArgs,
                        outputPath
                    );

                    SendRenderUpdate(30, "FFmpeg render işlemi başladı...", "ffmpeg " + fullArgs, false, "");

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = fullArgs,
                        WorkingDirectory = projectDir,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (Process proc = Process.Start(psi))
                    {
                        string line;
                        int step = 35;
                        while ((line = proc.StandardError.ReadLine()) != null)
                        {
                            if (line.Contains("frame=") || line.Contains("time=") || line.Contains("Error") || line.Contains("Output"))
                            {
                                step = Math.Min(95, step + 4);
                                SendRenderUpdate(step, "Video işleniyor (FFmpeg)...", line, false, "");
                            }
                        }
                        proc.WaitForExit();

                        if (proc.ExitCode == 0 && File.Exists(outputPath))
                        {
                            SendRenderUpdate(100, "✅ Render başarıyla tamamlandı! (" + outFileName + ")", "Dosya kaydedildi: " + outputPath, true, outputPath);
                            this.BeginInvoke(new Action(() => SetStatus("✅ Render tamamlandı: " + outputPath)));
                        }
                        else
                        {
                            SendRenderUpdate(100, "❌ FFmpeg render sırasında bir hata oluştu (ExitCode: " + proc.ExitCode + ")", "", false, "");
                        }
                    }
                }
                catch (Exception ex)
                {
                    SendRenderUpdate(100, "❌ Render hatası: " + ex.Message, ex.ToString(), false, "");
                }
            });
        }

        private void WriteSrtFile(string srtPath, List<Dictionary<string, object>> subClips)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < subClips.Count; i++)
            {
                var sc = subClips[i];
                double start = Math.Max(0, GetDouble(sc, "start", 0));
                double dur = Math.Max(0.4, GetDouble(sc, "duration", 3.0));
                double end = start + dur;
                string text = Convert.ToString(sc.ContainsKey("text") ? sc["text"] : "").Trim();
                if (string.IsNullOrEmpty(text)) continue;

                sb.AppendLine((i + 1).ToString());
                sb.AppendFormat("{0} --> {1}\r\n", FormatSrtTimestamp(start), FormatSrtTimestamp(end));
                sb.AppendLine(text);
                sb.AppendLine();
            }
            File.WriteAllText(srtPath, sb.ToString(), new UTF8Encoding(false));
        }

        private string FormatSrtTimestamp(double totalSec)
        {
            TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, totalSec));
            return string.Format("{0:D2}:{1:D2}:{2:D2},{3:D3}", ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds);
        }

        private void SendRenderUpdate(int percent, string status, string logLine, bool completed, string outputPath)
        {
            this.BeginInvoke(new Action(() =>
            {
                if (wvEditor == null || wvEditor.CoreWebView2 == null) return;
                var data = new Dictionary<string, object>
                {
                    { "percent", percent },
                    { "status", status },
                    { "logLine", logLine },
                    { "completed", completed },
                    { "outputPath", outputPath }
                };
                wvEditor.CoreWebView2.ExecuteScriptAsync(
                    string.Format("window.onHostRenderProgress && window.onHostRenderProgress({0});", json.Serialize(data)));
            }));
        }

        private List<Dictionary<string, object>> ToDictList(object obj)
        {
            var res = new List<Dictionary<string, object>>();
            IEnumerable enumerable = obj as IEnumerable;
            if (enumerable != null)
            {
                foreach (var item in enumerable)
                {
                    Dictionary<string, object> d = item as Dictionary<string, object>;
                    if (d != null) res.Add(d);
                }
            }
            return res;
        }

        private double GetDouble(Dictionary<string, object> d, string key, double defVal)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return defVal;
            try { return Convert.ToDouble(d[key], CultureInfo.InvariantCulture); }
            catch { return defVal; }
        }

        private void SetStatus(string text)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => lblStatus.Text = text));
                return;
            }
            lblStatus.Text = text;
        }

        private void UpdateStatusBarCounts()
        {
            try
            {
                int vCount = GetSortedProjectVideoFiles().Count;
                int aCount = Directory.Exists(audioDir)
                    ? Directory.GetFiles(audioDir).Count(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
                    : 0;
                int expected = GetExpectedSceneCountFromScript();
                lblCounts.Text = string.Format("Flow Videoları: {0}/{1} | Sesler: {2} | FFmpeg: Hazır", vCount, expected, aCount);
            }
            catch { }
        }
    }
}
