// Proxy Hotspot Share (共享代理)
// Share your (TUN-proxied) internet as a Windows mobile hotspot.
// C# 5.0 / .NET Framework 4.x / WinForms + NotifyIcon tray
// WinRT tethering APIs loaded fully at runtime (Type.GetType + dynamic) - zero winmd references.
// i18n: zh-CN / en, auto-detect OS language, in-app toggle, persisted in HKCU.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ProxyShareApp {

  static class I18N {
    public static string Lang = "zh";
    public static void Init() {
      try {
        object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\ProxyHotspotShare", "Lang", null);
        string s = v == null ? "" : v.ToString();
        if (s == "zh" || s == "en") { Lang = s; return; }
        Lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en";
      } catch { Lang = "zh"; }
    }
    public static void Toggle() {
      Lang = Lang == "zh" ? "en" : "zh";
      Save();
    }
    public static void SetLang(string l) {
      if (l == "zh" || l == "en") { Lang = l; Save(); }
    }
    public static void Save() {
      try {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey("Software\\ProxyHotspotShare")) {
          if (k != null) k.SetValue("Lang", Lang);
        }
      } catch { }
    }
    public static string T(string zh, string en) { return Lang == "zh" ? zh : en; }
    public static string F(string zh, string en, params object[] args) {
      return string.Format(Lang == "zh" ? zh : en, args);
    }
  }

  static class Program {
    [STAThread]
    static void Main() {
      I18N.Init();
      bool createdNew;
      using (Mutex mux = new Mutex(true, "Local\\ProxyShareAppSingle", out createdNew)) {
        if (!createdNew) {
          try {
            using (EventWaitHandle ev = EventWaitHandle.OpenExisting("Local\\ProxyShareAppShow")) { ev.Set(); }
          } catch {
            MessageBox.Show(I18N.T(
              "「共享代理」已在后台运行，请双击托盘里的绿色圆点图标打开界面。",
              "Proxy Hotspot Share is already running. Double-click the green dot icon in the tray to open it."),
              "Proxy Hotspot", MessageBoxButtons.OK, MessageBoxIcon.Information);
          }
          return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        GC.KeepAlive(mux);
      }
    }
  }

  static class Log {
    static object _lk = new object();
    public static void Write(string msg) {
      try {
        lock (_lk) {
          string dir = AppDomain.CurrentDomain.BaseDirectory;
          File.AppendAllText(Path.Combine(dir, "share.log"),
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
        }
      } catch { }
    }
  }

  // ---- WinRT runtime loading helpers ----
  static class WinRt {
    static Type _ext;
    static Type ExtType() {
      if (_ext == null) {
        Assembly a = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
        _ext = a.GetType("System.WindowsRuntimeSystemExtensions", true);
      }
      return _ext;
    }
    public static Type WinType(string fullName) {
      Type t = Type.GetType(fullName + ", Windows.Networking, ContentType=WindowsRuntime");
      if (t == null) throw new InvalidOperationException("WinRT type load failed: " + fullName);
      return t;
    }
    public static dynamic Await(dynamic asyncOp, string resultTypeName) {
      if (asyncOp == null) return null;
      Type rt = WinType(resultTypeName);
      foreach (MethodInfo m in ExtType().GetMethods()) {
        if (m.Name != "AsTask" || !m.IsGenericMethodDefinition) continue;
        ParameterInfo[] ps = m.GetParameters();
        if (ps.Length != 1 || ps[0].ParameterType.Name != "IAsyncOperation`1") continue;
        dynamic t = m.MakeGenericMethod(rt).Invoke(null, new object[] { (object)asyncOp });
        t.Wait(30000);
        return t.Result;
      }
      throw new InvalidOperationException("AsTask<IAsyncOperation<TResult>> not found");
    }
    public static IEnumerable GetProfiles() {
      Type t = WinType("Windows.Networking.Connectivity.NetworkInformation");
      object r = t.GetMethod("GetConnectionProfiles").Invoke(null, null);
      return (IEnumerable)r;
    }
    public static dynamic GetInternetProfile() {
      Type t = WinType("Windows.Networking.Connectivity.NetworkInformation");
      return t.GetMethod("GetInternetConnectionProfile").Invoke(null, null);
    }
    public static object Call(object target, string method) {
      if (target == null) return null;
      try {
        MethodInfo m = target.GetType().GetMethod(method);
        if (m == null) return null;
        return m.Invoke(target, null);
      } catch { return null; }
    }
    public static string GetStrProp(object target, string prop) {
      try {
        if (target == null) return null;
        PropertyInfo pi = target.GetType().GetProperty(prop);
        if (pi == null) return null;
        object v = pi.GetValue(target, null);
        return v == null ? null : v.ToString();
      } catch { return null; }
    }
    public static void SetProp(object target, string prop, object val) {
      try {
        if (target == null) return;
        PropertyInfo pi = target.GetType().GetProperty(prop);
        if (pi != null) pi.SetValue(target, val, null);
      } catch { }
    }
  }

  class DeviceRow {
    public string Ip; public string Mac; public string State; public int Type;
  }

  // Hotspot subnet auto-detection (ICS default 192.168.137.0/24, follows Wi-Fi Direct virtual adapter)
  static class HotspotNet {
    public static byte A = 192, B = 168, C = 137;
    static DateTime _last = DateTime.MinValue;
    public static void Refresh() {
      try {
        if ((DateTime.Now - _last).TotalSeconds < 10) return;
        _last = DateTime.Now;
        NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
        foreach (NetworkInterface i in nics) {
          if (i.OperationalStatus != OperationalStatus.Up) continue;
          string d = i.Description ?? "";
          string n = i.Name ?? "";
          if (d.IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) < 0 && !n.StartsWith("本地连接*")) continue;
          foreach (var a in i.GetIPProperties().UnicastAddresses) {
            if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            byte[] b = a.Address.GetAddressBytes();
            if (b.Length != 4) continue;
            if (b[3] == 0 || b[3] == 1 || b[3] == 255) continue;
            A = b[0]; B = b[1]; C = b[2];
            return;
          }
        }
      } catch { }
    }
  }

  static class Arp {
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_IPNETROW {
      public uint Index;
      public uint PhysAddrLen;
      [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
      public byte[] PhysAddr;
      public uint Addr;
      public uint Type;
    }
    [DllImport("iphlpapi.dll")]
    static extern uint GetIpNetTable(IntPtr table, ref uint size, bool order);

    public static List<MIB_IPNETROW> Rows() {
      List<MIB_IPNETROW> list = new List<MIB_IPNETROW>();
      uint size = 0;
      uint rc = GetIpNetTable(IntPtr.Zero, ref size, false);
      if (size == 0 || size > 8u * 1024 * 1024) return list;
      if (rc != 0 && rc != 122) return list; // 122=ERROR_INSUFFICIENT_BUFFER is the expected probe result
      IntPtr buf = Marshal.AllocHGlobal((int)size);
      try {
        if (GetIpNetTable(buf, ref size, true) == 0) {
          int n = Marshal.ReadInt32(buf);
          int rowSize = Marshal.SizeOf(typeof(MIB_IPNETROW));
          for (int i = 0; i < n; i++) {
            object o = Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + 4 + (long)i * rowSize), typeof(MIB_IPNETROW));
            if (o != null) list.Add((MIB_IPNETROW)o);
          }
        }
      } finally { Marshal.FreeHGlobal(buf); }
      return list;
    }

    public static List<DeviceRow> HotspotClients() {
      List<DeviceRow> res = new List<DeviceRow>();
      foreach (MIB_IPNETROW r in Rows()) {
        byte[] b = BitConverter.GetBytes(r.Addr);
        if (b.Length != 4) continue;
        if (b[0] != HotspotNet.A || b[1] != HotspotNet.B || b[2] != HotspotNet.C) continue;
        if (b[3] == 1 || b[3] == 255) continue;
        if (r.Type == 2) continue;
        string mac = "";
        int len = (int)Math.Min(r.PhysAddrLen, 8u);
        for (int i = 0; i < len; i++) mac += r.PhysAddr[i].ToString("X2") + (i < len - 1 ? "-" : "");
        if (mac.Length == 0) mac = "—";
        res.Add(new DeviceRow { Ip = new IPAddress(b).ToString(), Mac = mac, State = I18N.T("在线", "online"), Type = (int)r.Type });
      }
      return res;
    }
  }

  // ---- Mobile hotspot control (WinRT dynamic) ----
  static class Tethering {
    static dynamic _mgr;
    static object _lk = new object();
    const string OpResult = "Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult";

    public static string ShareProfileName() {
      dynamic p = FindShareProfile();
      if (p == null) return null;
      try { return p.ProfileName.ToString(); } catch { return null; }
    }

    public static dynamic FindShareProfile() {
      try {
        foreach (dynamic p in WinRt.GetProfiles()) {
          string n = "";
          try { n = (p.ProfileName ?? "").ToString(); } catch { }
          bool hit =
            n.IndexOf("0dcloud", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("meta", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("clash", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("mihomo", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("sing-box", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("tun", StringComparison.OrdinalIgnoreCase) >= 0;
          if (!hit) continue;
          bool none = false;
          try { none = p.GetNetworkConnectivityLevel().ToString() == "None"; } catch { }
          if (!none) return p;
        }
      } catch { }
      try { return WinRt.GetInternetProfile(); } catch { return null; }
    }

    public static dynamic GetManager(bool refresh) {
      lock (_lk) {
        if (_mgr != null && !refresh) return _mgr;
        dynamic p = FindShareProfile();
        if (p == null) return _mgr;
        try {
          Type t = WinRt.WinType("Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager");
          foreach (MethodInfo m in t.GetMethods()) {
            if (m.Name != "CreateFromConnectionProfile" || !m.IsStatic) continue;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length == 1) {
              try { _mgr = (dynamic)m.Invoke(null, new object[] { (object)p }); if (_mgr != null) return _mgr; } catch { }
            } else if (ps.Length == 2) {
              try {
                dynamic adapter = p.NetworkAdapter;
                if (adapter == null) continue;
                _mgr = (dynamic)m.Invoke(null, new object[] { (object)p, (object)adapter });
                if (_mgr != null) return _mgr;
              } catch { }
            }
          }
        } catch { }
        return _mgr;
      }
    }

    public static bool IsOn(dynamic mgr) {
      if (mgr == null) return false;
      try { return mgr.TetheringOperationalState.ToString() == "On"; } catch { return false; }
    }

    public static string StateText(dynamic mgr) {
      if (mgr == null) return "no manager";
      try { return mgr.TetheringOperationalState.ToString(); } catch { return "?"; }
    }

    public static string Start(dynamic mgr) {
      if (mgr == null) return I18N.T("开启失败：没有可共享的网络连接", "Start failed: no internet connection to share");
      try {
        dynamic r = WinRt.Await(mgr.StartTetheringAsync(), OpResult);
        if (r == null) return I18N.T("开启结果未知", "Start result unknown");
        if (r.Status.ToString() == "Success") return I18N.T("热点已开启 ✔", "Hotspot started ✔");
        return I18N.F("开启失败：{0} {1}", "Start failed: {0} {1}", r.Status.ToString(), (r.AdditionalErrorMessage ?? ""));
      } catch (Exception ex) { return I18N.T("开启异常：", "Start error: ") + ex.Message; }
    }

    public static string Stop(dynamic mgr) {
      if (mgr == null) return I18N.T("管理器不可用", "Tethering manager unavailable");
      try {
        dynamic r = WinRt.Await(mgr.StopTetheringAsync(), OpResult);
        if (r == null) return I18N.T("关闭结果未知", "Stop result unknown");
        return r.Status.ToString() == "Success" ? I18N.T("热点已关闭 ■", "Hotspot stopped ■")
          : I18N.F("关闭失败：{0}", "Stop failed: {0}", r.Status.ToString());
      } catch (Exception ex) { return I18N.T("关闭异常：", "Stop error: ") + ex.Message; }
    }

    static bool IsAscii(string s) {
      foreach (char c in s) if (c > 126) return false;
      return true;
    }

    public static string ApplyConfig(dynamic mgr, string ssid, string pass, out bool applied) {
      applied = false;
      if (mgr == null) return I18N.T("管理器不可用（先开启一次热点）", "Manager unavailable (start sharing once)");
      if (string.IsNullOrWhiteSpace(ssid)) return I18N.T("热点名不能为空", "Hotspot name cannot be empty");
      if (pass == null || pass.Length < 8 || pass.Length > 63) return I18N.T("密码长度需 8～63 位", "Password must be 8-63 characters");
      if (!IsAscii(ssid) || !IsAscii(pass)) return I18N.T("名称与密码请用英文数字符号（ASCII）", "Name and password must be ASCII");
      bool wasOn = IsOn(mgr);
      try {
        Type ct = WinRt.WinType("Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration");
        dynamic cfg = Activator.CreateInstance(ct);
        WinRt.SetProp(cfg, "Ssid", ssid);
        WinRt.SetProp(cfg, "Passphrase", pass);
        try { WinRt.Await(mgr.ConfigureAccessPointAsync(cfg), OpResult); }
        catch { /* some projections throw after the op was dispatched anyway */ }
        Thread.Sleep(600);
        object cur = WinRt.Call(mgr, "GetCurrentAccessPointConfiguration");
        if (cur != null) {
          string cs = WinRt.GetStrProp(cur, "Ssid");
          string cp = WinRt.GetStrProp(cur, "Passphrase");
          if (cs == ssid && cp == pass) applied = true;
        }
        if (!applied) return I18N.T("设置未生效（读取回执不匹配）", "Settings not applied (verification mismatch)");
        if (wasOn) {
          Stop(mgr);
          Thread.Sleep(1500);
          string rs = Start(mgr);
          return I18N.T("已保存并重启热点：", "Saved, hotspot restarted: ") + rs;
        }
        return I18N.T("已保存（下次开启热点时生效）", "Saved (takes effect on next start)");
      } catch (Exception ex) { return I18N.T("设置异常：", "Config error: ") + ex.Message; }
    }
  }

  class MainForm : Form {
    Label lblState, lblShareSrc, lblUpstream, lblProxy, lblSysProxy, lblSpeed;
    Button btnToggle, btnSave;
    TextBox txtSsid, txtPass;
    GroupBox grpHotspot, grpNet, grpDevices;
    ListView lvDevices;
    CheckBox chkKeep, chkAuto;
    StatusStrip status;
    ToolStripStatusLabel lblMsg;
    System.Windows.Forms.Timer uiTimer, netTimer, devTimer, bootTimer;
    NotifyIcon ni;
    ContextMenuStrip trayMenu;
    ToolStripMenuItem miOpen, miToggle, miExitKeep, miExitStop;
    Label lName, lPass;
    LinkLabel lblLang;

    bool desiredOn = true;
    bool busy = false;
    bool cfgDirty = false;
    bool realExit = false;
    bool hideTipShown = false;
    bool lastOn = false;
    string curSsid = "";
    int lastDevCount = 0;
    HashSet<string> lastDevices = new HashSet<string>();
    DateTime lastTry = DateTime.MinValue;
    NetworkInterface tunNic;
    long lastRx = -1, lastTx = -1;
    DateTime lastTick = DateTime.MinValue;

    static Bitmap _bmpOn, _bmpOff;
    static Icon _iconOn, _iconOff;

    public MainForm() {
      FormBorderStyle = FormBorderStyle.FixedSingle;
      MaximizeBox = false;
      StartPosition = FormStartPosition.CenterScreen;
      ClientSize = new System.Drawing.Size(536, 604);
      AutoScaleMode = AutoScaleMode.Dpi;
      Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
      FormClosing += new FormClosingEventHandler(OnFormClosing);

      lblState = MkLabel(14, 12, 380, 34, 15F, true);
      lblShareSrc = MkLabel(16, 50, 390, 18, 9F, false);
      Controls.Add(lblState);
      Controls.Add(lblShareSrc);
      btnToggle = new Button();
      btnToggle.Location = new System.Drawing.Point(412, 14);
      btnToggle.Size = new System.Drawing.Size(108, 34);
      btnToggle.Click += new EventHandler(OnToggle);
      Controls.Add(btnToggle);

      grpHotspot = new GroupBox();
      grpHotspot.Location = new System.Drawing.Point(12, 78);
      grpHotspot.Size = new System.Drawing.Size(512, 68);
      lName = MkLabel(12, 27, 46, 20, 9F, false); lName.AutoSize = true; grpHotspot.Controls.Add(lName);
      txtSsid = new TextBox(); txtSsid.Location = new System.Drawing.Point(58, 23); txtSsid.Width = 120;
      txtSsid.TextChanged += new EventHandler(MarkDirty); grpHotspot.Controls.Add(txtSsid);
      lPass = MkLabel(192, 27, 40, 20, 9F, false); lPass.AutoSize = true; grpHotspot.Controls.Add(lPass);
      txtPass = new TextBox(); txtPass.Location = new System.Drawing.Point(234, 23); txtPass.Width = 120;
      txtPass.TextChanged += new EventHandler(MarkDirty); grpHotspot.Controls.Add(txtPass);
      btnSave = new Button();
      btnSave.Location = new System.Drawing.Point(372, 21); btnSave.Size = new System.Drawing.Size(126, 27);
      btnSave.Click += new EventHandler(OnSave); grpHotspot.Controls.Add(btnSave);
      Controls.Add(grpHotspot);

      grpNet = new GroupBox();
      grpNet.Location = new System.Drawing.Point(12, 154);
      grpNet.Size = new System.Drawing.Size(512, 126);
      lblUpstream = MkLabel(14, 24, 484, 18, 9F, false);
      lblProxy = MkLabel(14, 46, 484, 18, 9F, false);
      lblSysProxy = MkLabel(14, 68, 484, 18, 9F, false);
      lblSpeed = MkLabel(14, 92, 484, 18, 9F, false);
      grpNet.Controls.Add(lblUpstream); grpNet.Controls.Add(lblProxy);
      grpNet.Controls.Add(lblSysProxy); grpNet.Controls.Add(lblSpeed);
      Controls.Add(grpNet);

      grpDevices = new GroupBox();
      grpDevices.Location = new System.Drawing.Point(12, 288);
      grpDevices.Size = new System.Drawing.Size(512, 244);
      lvDevices = new ListView();
      lvDevices.View = View.Details; lvDevices.FullRowSelect = true; lvDevices.ShowItemToolTips = true;
      lvDevices.Columns.Add("IP", 130); lvDevices.Columns.Add("MAC", 170); lvDevices.Columns.Add("St", 90);
      lvDevices.Location = new System.Drawing.Point(10, 24); lvDevices.Size = new System.Drawing.Size(490, 208);
      grpDevices.Controls.Add(lvDevices);
      Controls.Add(grpDevices);

      chkKeep = new CheckBox();
      chkKeep.Checked = true;
      chkKeep.Location = new System.Drawing.Point(14, 540); chkKeep.Size = new System.Drawing.Size(300, 22);
      Controls.Add(chkKeep);
      chkAuto = new CheckBox();
      chkAuto.Location = new System.Drawing.Point(330, 540); chkAuto.Size = new System.Drawing.Size(110, 22);
      chkAuto.CheckedChanged += new EventHandler(OnAutoStartChanged);
      Controls.Add(chkAuto);
      lblLang = new LinkLabel();
      lblLang.AutoSize = true;
      lblLang.Location = new System.Drawing.Point(446, 542);
      lblLang.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
      lblLang.ForeColor = Color.FromArgb(110, 110, 110);
      lblLang.LinkColor = Color.FromArgb(46, 120, 220);
      lblLang.ActiveLinkColor = Color.FromArgb(46, 120, 220);
      lblLang.LinkBehavior = LinkBehavior.HoverUnderline;
      lblLang.LinkClicked += new LinkLabelLinkClickedEventHandler(OnLangClick);
      Controls.Add(lblLang);

      status = new StatusStrip();
      lblMsg = new ToolStripStatusLabel();
      status.Items.Add(lblMsg);
      Controls.Add(status);

      BuildTray();
      RebuildTexts();
      SetStatus(I18N.T("就绪", "Ready"));

      uiTimer = new System.Windows.Forms.Timer(); uiTimer.Interval = 1000; uiTimer.Tick += new EventHandler(OnFastTick);
      netTimer = new System.Windows.Forms.Timer(); netTimer.Interval = 3000; netTimer.Tick += new EventHandler(OnSlowTick);
      devTimer = new System.Windows.Forms.Timer(); devTimer.Interval = 2000; devTimer.Tick += new EventHandler(OnDevTick);
      bootTimer = new System.Windows.Forms.Timer(); bootTimer.Interval = 700; bootTimer.Tick += new EventHandler(OnBoot);
      bootTimer.Start();

      // wake channel: re-launching the exe brings the window back
      Thread wake = new Thread(new ThreadStart(delegate {
        try {
          using (EventWaitHandle ev = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ProxyShareAppShow")) {
            while (true) {
              if (!ev.WaitOne()) break;
              try { BeginInvoke(new Action(delegate { Show(); Activate(); })); }
              catch { return; }
            }
          }
        } catch { }
      }));
      wake.IsBackground = true;
      wake.Start();

      Shown += new EventHandler(delegate(object s, EventArgs e) { Log.Write("UI start"); });
    }

    void RebuildTexts() {
      Text = I18N.T("共享代理 · Proxy Hotspot", "Proxy Hotspot Share");
      grpHotspot.Text = I18N.T("热点设置", "Hotspot settings");
      lName.Text = I18N.T("名称:", "Name:");
      lPass.Text = I18N.T("密码:", "Password:");
      btnSave.Text = I18N.T("保存并应用", "Save & apply");
      grpNet.Text = I18N.T("网络与代理状态", "Network & proxy status");
      chkKeep.Text = I18N.T("掉线自动重开（防 5 分钟无设备自动关）", "Auto-restart (prevent Windows auto-off)");
      chkAuto.Text = I18N.T("开机自启", "Start with Windows");
      miOpen.Text = I18N.T("打开主界面(&O)", "Open(&O)");
      miExitKeep.Text = I18N.T("退出（保持热点）", "Exit (keep hotspot)");
      miExitStop.Text = I18N.T("退出并关闭热点", "Exit & stop hotspot");
      ni.Text = I18N.T("共享代理", "Proxy Hotspot");
      RebuildLangLink();
      RepositionEdits();
      RebuildColumns();
      RefreshAll();
    }

    // 名称/密码标签随语言自适应宽度（"Password:" 比「密码:」长，需动态排版）
    void RepositionEdits() {
      int x = 12;
      lName.Location = new System.Drawing.Point(x, 28);
      txtSsid.Width = 88;
      txtSsid.Location = new System.Drawing.Point(x + lName.Width + 8, 23);
      int x2 = txtSsid.Right + 16;
      lPass.Location = new System.Drawing.Point(x2, 28);
      txtPass.Location = new System.Drawing.Point(x2 + lPass.Width + 8, 23);
      txtPass.Width = btnSave.Left - txtPass.Location.X - 12;
    }

    // 「中文 | EN」：当前语言灰色，另一个是可点链接
    void RebuildLangLink() {
      lblLang.Text = "中文 | EN";
      lblLang.Links.Clear();
      if (I18N.Lang == "zh") lblLang.Links.Add(5, 2);
      else lblLang.Links.Add(0, 2);
    }

    void OnLangClick(object s, LinkLabelLinkClickedEventArgs e) {
      int off = (e.Link == null) ? 5 : e.Link.Start;
      string target = off == 0 ? "zh" : "en";
      if (target == I18N.Lang) return;
      I18N.SetLang(target);
      Log.Write("language -> " + I18N.Lang);
      RebuildTexts();
    }

    void RebuildColumns() {
      lvDevices.Columns[0].Text = I18N.T("IP 地址", "IP address");
      lvDevices.Columns[1].Text = I18N.T("MAC 地址", "MAC address");
      lvDevices.Columns[2].Text = I18N.T("状态", "Status");
    }


    void BuildTray() {
      trayMenu = new ContextMenuStrip();
      miOpen = new ToolStripMenuItem();
      miOpen.Click += new EventHandler(delegate(object s, EventArgs e) { Show(); Activate(); });
      miToggle = new ToolStripMenuItem();
      miToggle.Click += new EventHandler(OnToggle);
      miExitKeep = new ToolStripMenuItem();
      miExitKeep.Click += new EventHandler(delegate(object s, EventArgs e) {
        realExit = true;
        try { ni.Visible = false; } catch { }
        Close();
      });
      miExitStop = new ToolStripMenuItem();
      miExitStop.Click += new EventHandler(OnExitStop);
      trayMenu.Items.Add(miOpen);
      trayMenu.Items.Add(miToggle);
      trayMenu.Items.Add(new ToolStripSeparator());
      trayMenu.Items.Add(miExitKeep);
      trayMenu.Items.Add(miExitStop);

      ni = new NotifyIcon();
      ni.Icon = DotIcon(false);
      ni.ContextMenuStrip = trayMenu;
      ni.Visible = true;
      ni.DoubleClick += new EventHandler(delegate(object s, EventArgs e) { Show(); Activate(); });
    }

    void OnExitStop(object s, EventArgs e) {
      SetStatus(I18N.T("正在关闭热点并退出…", "Stopping hotspot & exiting…"));
      dynamic mgr = Tethering.GetManager(false);
      Task<string> t = Task.Run(new Func<string>(delegate { return Tethering.Stop(mgr); }));
      try { t.Wait(8000); Log.Write("exit & stop -> " + t.Result); }
      catch { Log.Write("exit & stop -> timeout/error"); }
      realExit = true;
      try { ni.Visible = false; } catch { }
      Close();
    }

    static Icon DotIcon(bool on) {
      if (on && _iconOn != null) return _iconOn;
      if (!on && _iconOff != null) return _iconOff;
      Bitmap bmp = new Bitmap(16, 16);
      using (Graphics g = Graphics.FromImage(bmp)) {
        g.Clear(Color.Transparent);
        using (SolidBrush b = new SolidBrush(on ? Color.FromArgb(46, 160, 67) : Color.FromArgb(130, 130, 130)))
          g.FillEllipse(b, 2, 2, 12, 12);
        using (Pen p = new Pen(Color.White, 1.6f))
          g.DrawEllipse(p, 2, 2, 12, 12);
      }
      Icon ic = Icon.FromHandle(bmp.GetHicon());
      if (on) { _bmpOn = bmp; _iconOn = ic; } else { _bmpOff = bmp; _iconOff = ic; }
      return ic;
    }

    Label MkLabel(int x, int y, int w, int h, float size, bool bold) {
      Label l = new Label();
      l.Location = new System.Drawing.Point(x, y);
      l.Size = new System.Drawing.Size(w, h);
      l.AutoEllipsis = true;
      l.Font = new System.Drawing.Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
      return l;
    }

    void MarkDirty(object s, EventArgs e) { cfgDirty = true; }

    void OnFormClosing(object s, FormClosingEventArgs e) {
      if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing) {
        realExit = true;
      }
      if (!realExit) {
        e.Cancel = true;
        Hide();
        Log.Write("hidden to tray (hotspot keeps running)");
        if (!hideTipShown) {
          hideTipShown = true;
          try { ni.ShowBalloonTip(2500,
            I18N.T("共享代理 仍在后台", "Still running in background"),
            I18N.T("热点保持运行，双击托盘绿色圆点图标重新打开界面", "Hotspot keeps running. Double-click the green tray icon to reopen."),
            ToolTipIcon.Info); } catch { }
        }
        return;
      }
      Log.Write("app exit");
      try { ni.Visible = false; ni.Dispose(); } catch { }
    }

    void OnBoot(object s, EventArgs e) {
      bootTimer.Stop();
      LoadCurrentConfig();
      RefreshAll();
      desiredOn = true;
      dynamic mgr = Tethering.GetManager(false);
      Log.Write("OnBoot: state=" + Tethering.StateText(mgr));
      if (!Tethering.IsOn(mgr)) {
        SetStatus(I18N.T("正在自动开启共享…", "Auto-starting sharing…"));
        StartShare();
      } else {
        SetStatus(I18N.T("热点已在共享中", "Hotspot is already sharing"));
      }
      uiTimer.Start(); netTimer.Start(); devTimer.Start();
    }

    void LoadCurrentConfig() {
      try {
        dynamic mgr = Tethering.GetManager(false);
        if (mgr == null) { Log.Write("LoadConfig: no manager"); return; }
        object cur = WinRt.Call(mgr, "GetCurrentAccessPointConfiguration");
        if (cur == null) { Log.Write("LoadConfig: no current config"); return; }
        string s = WinRt.GetStrProp(cur, "Ssid");
        string p2 = WinRt.GetStrProp(cur, "Passphrase");
        Log.Write("LoadConfig: ssid=" + (s ?? "<null>") + " pass=" + (string.IsNullOrEmpty(p2) ? "<null>" : "(" + p2.Length + ")"));
        txtSsid.Text = s ?? "";
        txtPass.Text = p2 ?? "";
        curSsid = s ?? "";
        cfgDirty = false;
      } catch (Exception ex) { Log.Write("LoadConfig ex: " + ex.ToString()); }
    }

    void OnToggle(object s, EventArgs e) {
      if (busy) { SetStatus(I18N.T("正在处理，稍候…", "Busy, please wait…")); return; }
      dynamic mgr = Tethering.GetManager(false);
      if (Tethering.IsOn(mgr)) { desiredOn = false; StopShare(); }
      else { desiredOn = true; StartShare(); }
    }

    void StartShare() {
      busy = true;
      btnToggle.Enabled = false; miToggle.Enabled = false;
      SetStatus(I18N.T("正在开启热点（共享代理出口）…", "Starting hotspot (sharing proxy egress)…"));
      Task.Run(new Action(delegate {
        dynamic mgr = Tethering.GetManager(true);
        string res = Tethering.Start(mgr);
        Log.Write("StartShare -> " + res + " [profile: " + (Tethering.ShareProfileName() ?? "null") + "]");
        BeginInvoke(new Action(delegate {
          busy = false; btnToggle.Enabled = true; miToggle.Enabled = true;
          lastTry = DateTime.Now;
          SetStatus(res);
          RefreshAll();
        }));
      }));
    }

    void StopShare() {
      busy = true;
      btnToggle.Enabled = false; miToggle.Enabled = false;
      SetStatus(I18N.T("正在关闭热点…", "Stopping hotspot…"));
      Task.Run(new Action(delegate {
        dynamic mgr = Tethering.GetManager(false);
        string res = Tethering.Stop(mgr);
        Log.Write("StopShare -> " + res);
        BeginInvoke(new Action(delegate {
          busy = false; btnToggle.Enabled = true; miToggle.Enabled = true;
          SetStatus(I18N.F("{0}（设备将断开）", "{0} (devices will disconnect)", res));
          RefreshAll();
        }));
      }));
    }

    void OnSave(object s, EventArgs e) {
      if (busy) { SetStatus(I18N.T("正在处理，稍候…", "Busy, please wait…")); return; }
      string ssid = txtSsid.Text.Trim();
      string pass = txtPass.Text;
      busy = true; btnToggle.Enabled = false; btnSave.Enabled = false; miToggle.Enabled = false;
      SetStatus(I18N.T("正在保存热点配置…", "Saving hotspot settings…"));
      Task.Run(new Action(delegate {
        dynamic mgr = Tethering.GetManager(true);
        bool applied;
        string res = Tethering.ApplyConfig(mgr, ssid, pass, out applied);
        Log.Write("ApplyConfig(ssid=" + ssid + ") -> " + res);
        BeginInvoke(new Action(delegate {
          busy = false; btnToggle.Enabled = true; btnSave.Enabled = true; miToggle.Enabled = true;
          if (applied) cfgDirty = false;
          SetStatus(res);
          RefreshAll();
          LoadCurrentConfig();
        }));
      }));
    }

    void OnAutoStartChanged(object s, EventArgs e) {
      try {
        const string key = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(key, true)) {
          if (rk == null) return;
          if (chkAuto.Checked) {
            rk.SetValue("ProxyHotspotShare", "\"" + Application.ExecutablePath + "\"");
            SetStatus(I18N.T("已设置开机自启", "Autostart enabled"));
          } else {
            if (rk.GetValue("ProxyHotspotShare") != null) rk.DeleteValue("ProxyHotspotShare");
            SetStatus(I18N.T("已取消开机自启", "Autostart disabled"));
          }
        }
      } catch (Exception ex) { SetStatus(I18N.T("自启设置失败：", "Autostart setting failed: ") + ex.Message); }
    }

    void OnFastTick(object s, EventArgs e) {
      try {
        dynamic mgr = Tethering.GetManager(false);
        bool on = Tethering.IsOn(mgr);
        lblState.Text = on ? I18N.T("● 共享已开启", "● Sharing ON") : I18N.T("○ 共享已关闭", "○ Sharing OFF");
        lblState.ForeColor = on ? Color.Green : Color.Gray;
        btnToggle.Text = on ? I18N.T("关闭共享", "Stop sharing") : I18N.T("开启共享", "Start sharing");
        miToggle.Text = btnToggle.Text;
        UpdateShareSrc();
        UpdateSpeed();
        try {
          ni.Icon = DotIcon(on);
          ni.Text = on
            ? I18N.F("共享代理：开启 · {0} 台设备", "ProxyHotspot: ON · {0} device(s)", lastDevCount)
            : I18N.T("共享代理：关闭", "ProxyHotspot: OFF");
        } catch { }
        if (on != lastOn) {
          lastOn = on;
          try {
            string body = on
              ? (curSsid.Length > 0 ? I18N.F("手机可连接 {0}", "Connect your devices to {0}", curSsid)
                                    : I18N.T("热点已开始广播", "Hotspot is broadcasting"))
              : I18N.T("已连接设备将断开", "Connected devices will disconnect");
            ni.ShowBalloonTip(1500, on ? I18N.T("热点已开启", "Hotspot started") : I18N.T("热点已关闭", "Hotspot stopped"), body, ToolTipIcon.Info);
          } catch { }
        }
        if (!on && desiredOn && chkKeep.Checked && !busy &&
            (DateTime.Now - lastTry).TotalSeconds > 6) {
          lastTry = DateTime.Now;
          Log.Write("auto re-arm (state: " + Tethering.StateText(mgr) + ")");
          StartShare();
        }
      } catch { }
    }

    void OnSlowTick(object s, EventArgs e) {
      try {
        dynamic mgr = Tethering.GetManager(true);
        UpdateNetInfo();
      } catch { }
    }

    void OnDevTick(object s, EventArgs e) {
      try { UpdateDevices(); } catch { }
    }

    void RefreshAll() {
      UpdateNetInfo();
      UpdateDevices();
      OnFastTick(null, EventArgs.Empty);
    }

    void UpdateShareSrc() {
      try {
        NetworkInterface tun = FindTun();
        if (tun != null) {
          lblShareSrc.Text = I18N.F("共享出口：{0}（全部流量走代理隧道）", "Egress: {0} (all traffic via proxy tunnel)", tun.Name);
          lblShareSrc.ForeColor = Color.FromArgb(60, 120, 60);
        } else {
          lblShareSrc.Text = I18N.T("共享出口：直连（未检测到 TUN 代理，仍可共享网络）", "Egress: direct (no TUN proxy detected, still shareable)");
          lblShareSrc.ForeColor = Color.Gray;
        }
      } catch { }
    }

    static NetworkInterface FindNic(Func<NetworkInterface, bool> pred) {
      try {
        foreach (var i in NetworkInterface.GetAllNetworkInterfaces())
          if (pred(i)) return i;
      } catch { }
      return null;
    }

    static bool LooksLikeTun(NetworkInterface i) {
      if (i.OperationalStatus != OperationalStatus.Up) return false;
      string d = i.Description ?? ""; string n = i.Name ?? "";
      if (d.IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) >= 0) return false;
      if (n.StartsWith("本地连接*")) return false;
      if (n.StartsWith("vEthernet") || d.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0) return false;
      if (d.IndexOf("Meta Tunnel", StringComparison.OrdinalIgnoreCase) >= 0) return true;
      if (n.IndexOf("0dcloud", StringComparison.OrdinalIgnoreCase) >= 0) return true;
      if (d.IndexOf("wintun", StringComparison.OrdinalIgnoreCase) >= 0 ||
          d.IndexOf("WireGuard", StringComparison.OrdinalIgnoreCase) >= 0 ||
          d.IndexOf("TAP-Windows", StringComparison.OrdinalIgnoreCase) >= 0 ||
          d.IndexOf("TUN", StringComparison.OrdinalIgnoreCase) >= 0) return true;
      return false;
    }

    static NetworkInterface FindTun() { return FindNic(LooksLikeTun); }

    static string GetIpv4(NetworkInterface nic) {
      try {
        foreach (var a in nic.GetIPProperties().UnicastAddresses)
          if (a.Address.AddressFamily == AddressFamily.InterNetwork) return a.Address.ToString();
      } catch { }
      return "-";
    }

    static readonly int[] ProbePorts = { 7890, 7897, 7891, 17891, 2080, 1080, 10808 };
    static int _alivePort = 0;
    static DateTime _probeAt = DateTime.MinValue;
    static void ProbePortsAsync() {
      try {
        if ((DateTime.Now - _probeAt).TotalSeconds < 3) return;
        _probeAt = DateTime.Now;
        Task.Run(new Action(delegate {
          int p = 0;
          foreach (int pp in ProbePorts) {
            if (TcpProbe("127.0.0.1", pp, 300)) { p = pp; break; }
          }
          _alivePort = p;
        }));
      } catch { }
    }

    void UpdateNetInfo() {
      try {
        NetworkInterface wlan = FindNic(delegate(NetworkInterface i) {
          return i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                 i.OperationalStatus == OperationalStatus.Up &&
                 (i.Description ?? "").IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) < 0;
        });
        if (wlan != null) {
          string ssid = "";
          int bars = 0;
          try {
            Guid g = new Guid(wlan.Id);
            foreach (dynamic p in WinRt.GetProfiles()) {
              dynamic ad = null;
              try { ad = p.NetworkAdapter; } catch { }
              if (ad == null) continue;
              Guid na = Guid.Empty;
              try { na = (Guid)ad.NetworkAdapterId; } catch { continue; }
              if (na != g) continue;
              bool none = false;
              try { none = p.GetNetworkConnectivityLevel().ToString() == "None"; } catch { }
              if (none) continue;
              try { ssid = (p.ProfileName ?? "").ToString(); } catch { }
              try { bars = (int)(byte)p.GetSignalBars(); } catch { }
              break;
            }
          } catch { }
          lblUpstream.Text = I18N.F("上游网络：{0}（WLAN）  信号 {1}/5  IP：{2}", "Upstream: {0} (WLAN)  signal {1}/5  IP: {2}",
            string.IsNullOrEmpty(ssid) ? I18N.T("(未知)", "(unknown)") : ssid, bars, GetIpv4(wlan));
        } else {
          lblUpstream.Text = I18N.T("上游网络：未检测到 WLAN 连接", "Upstream: no WLAN connection detected");
        }

        NetworkInterface tun = FindTun();
        tunNic = tun;
        if (tun != null) {
          lblProxy.Text = I18N.F("代理状态：已开启（TUN：{0}，IP {1}）", "Proxy: ON (TUN: {0}, IP {1})", tun.Name, GetIpv4(tun));
          lblProxy.ForeColor = Color.FromArgb(60, 120, 60);
        } else {
          lblProxy.Text = I18N.T("代理状态：未检测到代理（将共享直连网络）", "Proxy: not detected (sharing direct connection)");
          lblProxy.ForeColor = Color.Gray;
        }

        string sysProxy = I18N.T("未设置", "off"); int enable = 0; string server = "";
        try {
          object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable", null);
          if (v != null) enable = (int)v;
          object sv = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyServer", null);
          if (sv != null) server = sv.ToString();
        } catch { }
        if (enable == 1 && !string.IsNullOrEmpty(server)) sysProxy = server;
        ProbePortsAsync();
        int alivePort = _alivePort;
        lblSysProxy.Text = I18N.F("系统代理：{0} · 本地核心端口：{1}", "System proxy: {0} · local core port: {1}", sysProxy,
          alivePort > 0 ? I18N.F("{0} 活跃 ✔", "{0} active ✔", alivePort)
                        : I18N.T("未监听（无代理或非默认端口）", "not listening (no proxy / non-default port)"));
        lblSysProxy.ForeColor = alivePort > 0 ? Color.FromArgb(60, 120, 60) : Color.Gray;
      } catch { }
    }

    static bool TcpProbe(string host, int port, int ms) {
      try {
        using (TcpClient c = new TcpClient()) {
          IAsyncResult ar = c.BeginConnect(host, port, null, null);
          bool ok = ar.AsyncWaitHandle.WaitOne(ms);
          if (ok && c.Connected) { c.EndConnect(ar); return true; }
          return false;
        }
      } catch { return false; }
    }

    void UpdateSpeed() {
      try {
        if (tunNic == null) { lblSpeed.Text = I18N.T("TUN 吞吐：—（无代理网卡）", "TUN throughput: — (no proxy adapter)"); return; }
        IPv4InterfaceStatistics st = tunNic.GetIPv4Statistics();
        long rx = st.BytesReceived, tx = st.BytesSent;
        string line = I18N.F("TUN 吞吐：↓ — ↑ —（累计 ↓ {0} ↑ {1}，含本机流量）", "TUN throughput: ↓ — ↑ — (total ↓ {0} ↑ {1})", FmtBytes(rx), FmtBytes(tx));
        if (lastRx >= 0 && lastTick != DateTime.MinValue) {
          double dt = (DateTime.Now - lastTick).TotalSeconds;
          if (dt > 0.2) {
            double rxd = (rx - lastRx) / dt, txd = (tx - lastTx) / dt;
            if (rxd >= 0 && txd >= 0)
              line = I18N.F("TUN 吞吐：↓ {0}/s ↑ {1}/s（累计 ↓ {2} ↑ {3}，含本机流量）", "TUN throughput: ↓ {0}/s ↑ {1}/s (total ↓ {2} ↑ {3})",
                FmtRate(rxd), FmtRate(txd), FmtBytes(rx), FmtBytes(tx));
          }
        }
        lastRx = rx; lastTx = tx; lastTick = DateTime.Now;
        lblSpeed.Text = line;
      } catch { }
    }

    static string FmtBytes(double v) {
      if (v > 1024 * 1024 * 1024) return (v / 1024 / 1024 / 1024).ToString("0.0") + " GB";
      if (v > 1024 * 1024) return (v / 1024 / 1024).ToString("0.0") + " MB";
      if (v > 1024) return (v / 1024).ToString("0.0") + " KB";
      return v.ToString("0") + " B";
    }
    static string FmtRate(double v) {
      if (v > 1024 * 1024) return (v / 1024 / 1024).ToString("0.0") + " MB";
      if (v > 1024) return (v / 1024).ToString("0.0") + " KB";
      return v.ToString("0") + " B";
    }

    static DateTime _lastPing = DateTime.MinValue;
    static void RefreshArp() {
      try {
        if ((DateTime.Now - _lastPing).TotalSeconds < 2) return;
        _lastPing = DateTime.Now;
        string bc = HotspotNet.A + "." + HotspotNet.B + "." + HotspotNet.C + ".255";
        Task.Run(new Action(delegate {
          try { using (Ping p = new Ping()) { p.Send(bc, 600); } } catch { }
        }));
      } catch { }
    }

    void UpdateDevices() {
      try {
        HotspotNet.Refresh();
        RefreshArp();
        List<DeviceRow> devs = Arp.HotspotClients();
        HashSet<string> now = new HashSet<string>();
        foreach (DeviceRow d in devs) now.Add(d.Ip);
        if (!now.SetEquals(lastDevices)) {
          List<string> parts = new List<string>();
          foreach (DeviceRow d in devs) parts.Add(d.Ip + "(type" + d.Type + "," + d.Mac + ")");
          Log.Write("devices changed: " + devs.Count + " [" + string.Join("; ", parts.ToArray()) + "]");
          foreach (string ip in now) {
            if (!lastDevices.Contains(ip)) {
              try { ni.ShowBalloonTip(2000, I18N.T("设备接入", "Device connected"),
                I18N.F("{0} 已连上热点", "{0} joined the hotspot", ip), ToolTipIcon.Info); } catch { }
            }
          }
          lastDevices = now;
        }
        lastDevCount = devs.Count;
        lvDevices.BeginUpdate();
        lvDevices.Items.Clear();
        if (devs.Count == 0) {
          ListViewItem it = new ListViewItem(I18N.T("（暂无设备连接）", "(no devices connected)"));
          it.SubItems.Add(""); it.SubItems.Add("");
          it.ForeColor = Color.Gray;
          lvDevices.Items.Add(it);
        } else {
          foreach (DeviceRow d in devs) {
            ListViewItem it = new ListViewItem(d.Ip);
            it.SubItems.Add(d.Mac);
            it.SubItems.Add(d.State);
            lvDevices.Items.Add(it);
          }
        }
        lvDevices.EndUpdate();
        grpDevices.Text = I18N.F("已连接设备（{0}）", "Connected devices ({0})", devs.Count);
      } catch { }
    }

    void SetStatus(string s) {
      try { lblMsg.Text = s; } catch { }
    }
  }
}
