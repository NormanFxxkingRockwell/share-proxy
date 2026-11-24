// 代理热点共享 (ProxyShare)
// 共享 0dcloud (Clash TUN) 的代理网络给手机等设备
// C# 5.0 / .NET Framework 4.x / WinForms + NotifyIcon 托盘常驻
// WinRT 移动热点 API 全部运行时加载（Type.GetType + dynamic），编译期零 winmd 依赖
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

  static class Program {
    [STAThread]
    static void Main() {
      bool createdNew;
      using (Mutex mux = new Mutex(true, "Local\\ProxyShareAppSingle", out createdNew)) {
        if (!createdNew) {
          // 已在后台运行：通知现有实例弹出窗口（而不是弹提示框）
          try {
            using (EventWaitHandle ev = EventWaitHandle.OpenExisting("Local\\ProxyShareAppShow")) { ev.Set(); }
          } catch {
            MessageBox.Show("「共享代理」已在后台运行，请双击托盘里的绿色圆点图标打开界面。", "共享代理", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

  // ---- WinRT 运行时加载工具 ----
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
      if (t == null) throw new InvalidOperationException("WinRT 类型加载失败: " + fullName);
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
      throw new InvalidOperationException("AsTask<IAsyncOperation<TResult>> 未找到");
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

  // 热点子网动态检测（默认 ICS 为 192.168.137.0/24，随 Wi-Fi Direct 虚拟网卡实际地址自适应）
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
      if (rc != 0 && rc != 122) return list; // 122=ERROR_INSUFFICIENT_BUFFER，探测大小时的预期返回值
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

    // 热点子网客户端：排除网关/广播，仅排除 Invalid(2)
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
        res.Add(new DeviceRow { Ip = new IPAddress(b).ToString(), Mac = mac, State = "在线", Type = (int)r.Type });
      }
      return res;
    }
  }

  // ---- 移动热点控制（WinRT dynamic）----
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
      if (mgr == null) return "无管理器";
      try { return mgr.TetheringOperationalState.ToString(); } catch { return "?"; }
    }

    public static string Start(dynamic mgr) {
      if (mgr == null) return "开启失败：没有可共享的网络连接";
      try {
        dynamic r = WinRt.Await(mgr.StartTetheringAsync(), OpResult);
        if (r == null) return "开启结果未知";
        if (r.Status.ToString() == "Success") return "热点已开启 ✔";
        return "开启失败：" + r.Status.ToString() + " " + (r.AdditionalErrorMessage ?? "");
      } catch (Exception ex) { return "开启异常：" + ex.Message; }
    }

    public static string Stop(dynamic mgr) {
      if (mgr == null) return "管理器不可用";
      try {
        dynamic r = WinRt.Await(mgr.StopTetheringAsync(), OpResult);
        if (r == null) return "关闭结果未知";
        return r.Status.ToString() == "Success" ? "热点已关闭 ■" : "关闭失败：" + r.Status.ToString();
      } catch (Exception ex) { return "关闭异常：" + ex.Message; }
    }

    static bool IsAscii(string s) {
      foreach (char c in s) if (c > 126) return false;
      return true;
    }

    public static string ApplyConfig(dynamic mgr, string ssid, string pass, out bool applied) {
      applied = false;
      if (mgr == null) return "管理器不可用（先开启一次热点）";
      if (string.IsNullOrWhiteSpace(ssid)) return "热点名不能为空";
      if (pass == null || pass.Length < 8 || pass.Length > 63) return "密码长度需 8～63 位";
      if (!IsAscii(ssid) || !IsAscii(pass)) return "名称与密码请用英文数字符号（ASCII）";
      bool wasOn = IsOn(mgr);
      try {
        Type ct = WinRt.WinType("Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration");
        dynamic cfg = Activator.CreateInstance(ct);
        WinRt.SetProp(cfg, "Ssid", ssid);
        WinRt.SetProp(cfg, "Passphrase", pass);
        try { WinRt.Await(mgr.ConfigureAccessPointAsync(cfg), OpResult); }
        catch { /* 个别环境投影异常，但操作本身已派发 */ }
        Thread.Sleep(600);
        object cur = WinRt.Call(mgr, "GetCurrentAccessPointConfiguration");
        if (cur != null) {
          string cs = WinRt.GetStrProp(cur, "Ssid");
          string cp = WinRt.GetStrProp(cur, "Passphrase");
          if (cs == ssid && cp == pass) applied = true;
        }
        if (!applied) return "设置未生效（读取回执不匹配）";
        if (wasOn) {
          Stop(mgr);
          Thread.Sleep(1500);
          string rs = Start(mgr);
          return "已保存并重启热点：" + rs;
        }
        return "已保存（下次开启热点时生效）";
      } catch (Exception ex) { return "设置异常：" + ex.Message; }
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
    ToolStripMenuItem miToggle;

    bool desiredOn = true;     // 双击打开即自动共享
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
      Text = "共享代理 · Proxy Hotspot";
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
      btnToggle.Text = "开启共享";
      btnToggle.Location = new System.Drawing.Point(412, 14);
      btnToggle.Size = new System.Drawing.Size(108, 34);
      btnToggle.Click += new EventHandler(OnToggle);
      Controls.Add(btnToggle);

      grpHotspot = new GroupBox();
      grpHotspot.Text = "热点设置";
      grpHotspot.Location = new System.Drawing.Point(12, 78);
      grpHotspot.Size = new System.Drawing.Size(512, 68);
      Label l1 = MkLabel(12, 27, 46, 20, 9F, false); l1.Text = "名称:"; grpHotspot.Controls.Add(l1);
      txtSsid = new TextBox(); txtSsid.Location = new System.Drawing.Point(58, 23); txtSsid.Width = 120;
      txtSsid.TextChanged += new EventHandler(MarkDirty); grpHotspot.Controls.Add(txtSsid);
      Label l2 = MkLabel(192, 27, 40, 20, 9F, false); l2.Text = "密码:"; grpHotspot.Controls.Add(l2);
      txtPass = new TextBox(); txtPass.Location = new System.Drawing.Point(234, 23); txtPass.Width = 120;
      txtPass.TextChanged += new EventHandler(MarkDirty); grpHotspot.Controls.Add(txtPass);
      btnSave = new Button(); btnSave.Text = "保存并应用";
      btnSave.Location = new System.Drawing.Point(372, 21); btnSave.Size = new System.Drawing.Size(126, 27);
      btnSave.Click += new EventHandler(OnSave); grpHotspot.Controls.Add(btnSave);
      Controls.Add(grpHotspot);

      grpNet = new GroupBox();
      grpNet.Text = "网络与代理状态";
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
      grpDevices.Text = "已连接设备（0）";
      grpDevices.Location = new System.Drawing.Point(12, 288);
      grpDevices.Size = new System.Drawing.Size(512, 244);
      lvDevices = new ListView();
      lvDevices.View = View.Details; lvDevices.FullRowSelect = true; lvDevices.ShowItemToolTips = true;
      lvDevices.Columns.Add("IP 地址", 130); lvDevices.Columns.Add("MAC 地址", 170); lvDevices.Columns.Add("状态", 90);
      lvDevices.Location = new System.Drawing.Point(10, 24); lvDevices.Size = new System.Drawing.Size(490, 208);
      grpDevices.Controls.Add(lvDevices);
      Controls.Add(grpDevices);

      chkKeep = new CheckBox();
      chkKeep.Text = "掉线自动重开（防 5 分钟无设备自动关）";
      chkKeep.Checked = true;
      chkKeep.Location = new System.Drawing.Point(14, 540); chkKeep.Size = new System.Drawing.Size(300, 22);
      Controls.Add(chkKeep);
      chkAuto = new CheckBox();
      chkAuto.Text = "开机自启";
      chkAuto.Location = new System.Drawing.Point(330, 540); chkAuto.Size = new System.Drawing.Size(110, 22);
      chkAuto.CheckedChanged += new EventHandler(OnAutoStartChanged);
      Controls.Add(chkAuto);

      status = new StatusStrip();
      lblMsg = new ToolStripStatusLabel("就绪");
      status.Items.Add(lblMsg);
      Controls.Add(status);

      BuildTray();

      uiTimer = new System.Windows.Forms.Timer(); uiTimer.Interval = 1000; uiTimer.Tick += new EventHandler(OnFastTick);
      netTimer = new System.Windows.Forms.Timer(); netTimer.Interval = 3000; netTimer.Tick += new EventHandler(OnSlowTick);
      devTimer = new System.Windows.Forms.Timer(); devTimer.Interval = 2000; devTimer.Tick += new EventHandler(OnDevTick);
      bootTimer = new System.Windows.Forms.Timer(); bootTimer.Interval = 700; bootTimer.Tick += new EventHandler(OnBoot);
      bootTimer.Start();

      // 后台等待“唤醒”信号：再次双击 exe 时弹出窗口
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

      Shown += new EventHandler(delegate(object s, EventArgs e) { Log.Write("UI 启动"); });
    }

    void BuildTray() {
      trayMenu = new ContextMenuStrip();
      ToolStripMenuItem miOpen = new ToolStripMenuItem("打开主界面(&O)");
      miOpen.Click += new EventHandler(delegate(object s, EventArgs e) { Show(); Activate(); });
      miToggle = new ToolStripMenuItem("开启共享");
      miToggle.Click += new EventHandler(OnToggle);
      ToolStripMenuItem miExitKeep = new ToolStripMenuItem("退出（保持热点）");
      miExitKeep.Click += new EventHandler(delegate(object s, EventArgs e) {
        realExit = true;
        try { ni.Visible = false; } catch { }
        Close();
      });
      ToolStripMenuItem miExitStop = new ToolStripMenuItem("退出并关闭热点");
      miExitStop.Click += new EventHandler(OnExitStop);
      trayMenu.Items.Add(miOpen);
      trayMenu.Items.Add(miToggle);
      trayMenu.Items.Add(new ToolStripSeparator());
      trayMenu.Items.Add(miExitKeep);
      trayMenu.Items.Add(miExitStop);

      ni = new NotifyIcon();
      ni.Icon = DotIcon(false);
      ni.Text = "共享代理";
      ni.ContextMenuStrip = trayMenu;
      ni.Visible = true;
      ni.DoubleClick += new EventHandler(delegate(object s, EventArgs e) { Show(); Activate(); });
    }

    void OnExitStop(object s, EventArgs e) {
      SetStatus("正在关闭热点并退出…");
      dynamic mgr = Tethering.GetManager(false);
      Task<string> t = Task.Run(new Func<string>(delegate { return Tethering.Stop(mgr); }));
      try { t.Wait(8000); Log.Write("退出并关闭热点 -> " + t.Result); }
      catch { Log.Write("退出并关闭热点 -> 超时/异常"); }
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
        Log.Write("界面隐藏到托盘（热点与保活继续运行）");
        if (!hideTipShown) {
          hideTipShown = true;
          try { ni.ShowBalloonTip(2500, "共享代理 仍在后台", "热点保持运行，双击托盘绿色圆点图标重新打开界面", ToolTipIcon.Info); } catch { }
        }
        return;
      }
      Log.Write("程序退出");
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
        SetStatus("正在自动开启共享…");
        StartShare();
      } else {
        SetStatus("热点已在共享中");
      }
      uiTimer.Start(); netTimer.Start(); devTimer.Start();
    }

    void LoadCurrentConfig() {
      try {
        dynamic mgr = Tethering.GetManager(false);
        if (mgr == null) { Log.Write("LoadConfig: 管理器不可用"); return; }
        object cur = WinRt.Call(mgr, "GetCurrentAccessPointConfiguration");
        if (cur == null) { Log.Write("LoadConfig: 无当前热点配置"); return; }
        string s = WinRt.GetStrProp(cur, "Ssid");
        string p2 = WinRt.GetStrProp(cur, "Passphrase");
        Log.Write("LoadConfig: ssid=" + (s ?? "<null>") + " pass=" + (string.IsNullOrEmpty(p2) ? "<null>" : "(" + p2.Length + "位)"));
        txtSsid.Text = s ?? "";
        txtPass.Text = p2 ?? "";
        curSsid = s ?? "";
        cfgDirty = false;
      } catch (Exception ex) { Log.Write("LoadConfig 异常: " + ex.ToString()); }
    }

    void OnToggle(object s, EventArgs e) {
      if (busy) { SetStatus("正在处理，稍候…"); return; }
      dynamic mgr = Tethering.GetManager(false);
      if (Tethering.IsOn(mgr)) { desiredOn = false; StopShare(); }
      else { desiredOn = true; StartShare(); }
    }

    void StartShare() {
      busy = true;
      btnToggle.Enabled = false; miToggle.Enabled = false;
      SetStatus("正在开启热点（共享 0dcloud 代理出口）…");
      Task.Run(new Action(delegate {
        dynamic mgr = Tethering.GetManager(true);
        string res = Tethering.Start(mgr);
        Log.Write("StartShare -> " + res + " [share profile: " + (Tethering.ShareProfileName() ?? "null") + "]");
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
      SetStatus("正在关闭热点…");
      Task.Run(new Action(delegate {
        dynamic mgr = Tethering.GetManager(false);
        string res = Tethering.Stop(mgr);
        Log.Write("StopShare -> " + res);
        BeginInvoke(new Action(delegate {
          busy = false; btnToggle.Enabled = true; miToggle.Enabled = true;
          SetStatus(res + "（设备将断开）");
          RefreshAll();
        }));
      }));
    }

    void OnSave(object s, EventArgs e) {
      if (busy) { SetStatus("正在处理，稍候…"); return; }
      string ssid = txtSsid.Text.Trim();
      string pass = txtPass.Text;
      busy = true; btnToggle.Enabled = false; btnSave.Enabled = false; miToggle.Enabled = false;
      SetStatus("正在保存热点配置…");
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
            SetStatus("已设置开机自启");
          } else {
            if (rk.GetValue("ProxyHotspotShare") != null) rk.DeleteValue("ProxyHotspotShare");
            SetStatus("已取消开机自启");
          }
        }
      } catch (Exception ex) { SetStatus("自启设置失败：" + ex.Message); }
    }

    void OnFastTick(object s, EventArgs e) {
      try {
        dynamic mgr = Tethering.GetManager(false);
        bool on = Tethering.IsOn(mgr);
        lblState.Text = on ? "● 共享已开启" : "○ 共享已关闭";
        lblState.ForeColor = on ? Color.Green : Color.Gray;
        btnToggle.Text = on ? "关闭共享" : "开启共享";
        miToggle.Text = on ? "关闭共享" : "开启共享";
        UpdateShareSrc();
        UpdateSpeed();
        // 托盘图标/气泡
        try {
          ni.Icon = DotIcon(on);
          ni.Text = on ? ("共享代理：开启 · " + lastDevCount + " 台设备" + (curSsid.Length > 0 ? " · " + curSsid : "")) : "共享代理：关闭";
        } catch { }
        if (on != lastOn) {
          lastOn = on;
          try { ni.ShowBalloonTip(1500, on ? "热点已开启" : "热点已关闭",
                 on ? (curSsid.Length > 0 ? "手机可连接 " + curSsid : "热点已开始广播") : "已连接设备将断开", ToolTipIcon.Info); } catch { }
        }
        if (!on && desiredOn && chkKeep.Checked && !busy &&
            (DateTime.Now - lastTry).TotalSeconds > 6) {
          lastTry = DateTime.Now;
          Log.Write("自动重开热点（状态: " + Tethering.StateText(mgr) + "）");
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
          lblShareSrc.Text = "共享出口：" + tun.Name + "（全部流量走代理隧道）";
          lblShareSrc.ForeColor = Color.FromArgb(60, 120, 60);
        } else {
          lblShareSrc.Text = "共享出口：直连（未检测到 TUN 代理，仍可共享网络）";
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
          lblUpstream.Text = string.Format("上游网络：{0}（WLAN）  信号 {1}/5  IP：{2}",
            string.IsNullOrEmpty(ssid) ? "(未知)" : ssid, bars, GetIpv4(wlan));
        } else {
          lblUpstream.Text = "上游网络：未检测到 WLAN 连接";
        }

        NetworkInterface tun = FindTun();
        tunNic = tun;
        if (tun != null) {
          lblProxy.Text = "代理状态：已开启（TUN：" + tun.Name + "，IP " + GetIpv4(tun) + "）";
          lblProxy.ForeColor = Color.FromArgb(60, 120, 60);
        } else {
          lblProxy.Text = "代理状态：未检测到代理（将共享直连网络）";
          lblProxy.ForeColor = Color.Gray;
        }

        string sysProxy = "未设置"; int enable = 0; string server = "";
        try {
          object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable", null);
          if (v != null) enable = (int)v;
          object sv = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyServer", null);
          if (sv != null) server = sv.ToString();
        } catch { }
        if (enable == 1 && !string.IsNullOrEmpty(server)) sysProxy = server;
        int alivePort = 0;
        foreach (int pp in ProbePorts) {
          if (TcpProbe("127.0.0.1", pp, 200)) { alivePort = pp; break; }
        }
        lblSysProxy.Text = string.Format("系统代理：{0} · 本地核心端口：{1}", sysProxy,
          alivePort > 0 ? (alivePort + " 活跃 ✔") : "未监听（无代理或非默认端口）");
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

    static readonly int[] ProbePorts = { 7890, 7897, 7891, 17891, 2080, 1080, 10808 };

    void UpdateSpeed() {
      try {
        if (tunNic == null) { lblSpeed.Text = "TUN 吞吐：—（无代理网卡）"; return; }
        IPv4InterfaceStatistics st = tunNic.GetIPv4Statistics();
        long rx = st.BytesReceived, tx = st.BytesSent;
        string line = "TUN 吞吐：↓ — ↑ —（累计 ↓ " + FmtBytes(rx) + " ↑ " + FmtBytes(tx) + "，含本机流量）";
        if (lastRx >= 0 && lastTick != DateTime.MinValue) {
          double dt = (DateTime.Now - lastTick).TotalSeconds;
          if (dt > 0.2) {
            double rxd = (rx - lastRx) / dt, txd = (tx - lastTx) / dt;
            if (rxd >= 0 && txd >= 0)
              line = string.Format("TUN 吞吐：↓ {0}/s ↑ {1}/s（累计 ↓ {2} ↑ {3}，含本机流量）",
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
        // 变化检测 + 日志/气泡
        HashSet<string> now = new HashSet<string>();
        foreach (DeviceRow d in devs) now.Add(d.Ip);
        if (!now.SetEquals(lastDevices)) {
          List<string> parts = new List<string>();
          foreach (DeviceRow d in devs) parts.Add(d.Ip + "(type" + d.Type + "," + d.Mac + ")");
          Log.Write("设备变化: " + devs.Count + " 台 [" + string.Join("; ", parts.ToArray()) + "]");
          foreach (string ip in now) {
            if (!lastDevices.Contains(ip)) {
              try { ni.ShowBalloonTip(2000, "设备接入", ip + " 已连上热点", ToolTipIcon.Info); } catch { }
            }
          }
          lastDevices = now;
        }
        lastDevCount = devs.Count;
        lvDevices.BeginUpdate();
        lvDevices.Items.Clear();
        if (devs.Count == 0) {
          ListViewItem it = new ListViewItem("（暂无设备连接）");
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
        grpDevices.Text = string.Format("已连接设备（{0}）", devs.Count);
      } catch { }
    }

    void SetStatus(string s) {
      try { lblMsg.Text = s; } catch { }
    }
  }
}
