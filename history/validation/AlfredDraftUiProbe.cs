using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

sealed class AlfredDraftUiProbe : Form {
    readonly Label status = new Label();
    readonly string report = @"F:\Alfred\build\ui-draft-check.txt";
    bool running;

    AlfredDraftUiProbe() {
        Text = "Alfred local draft UI test";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new System.Drawing.Size(540, 120);
        status.Text = "Local-only test: focus the separate Alfred test editor, then press F8.";
        status.SetBounds(20, 38, 500, 45);
        Controls.Add(status);
        Shown += delegate { if (!RegisterHotKey(Handle, 1, 0, 0x77)) status.Text = "Could not register F8."; };
        FormClosed += delegate { UnregisterHotKey(Handle, 1); };
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x312 && m.WParam.ToInt32() == 1) { BeginCheck(GetForegroundWindow()); return; }
        base.WndProc(ref m);
    }

    void BeginCheck(IntPtr targetWindow) {
        if (running) return;
        running = true;
        status.Text = "Checking the focused local editor; no message will be sent.";
        ThreadPool.QueueUserWorkItem(delegate {
            string result;
            try {
                Assembly app = Assembly.LoadFrom(@"F:\Alfred\Alfred.exe");
                Type targetType = app.GetType("Alfred.FocusedTextTarget", true);
                MethodInfo capture = targetType.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static);
                object target = capture.Invoke(null, new object[] { targetWindow, new Func<bool>(delegate { return false; }), false });
                MethodInfo read = targetType.GetMethod("Read", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo replace = targetType.GetMethod("Replace", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo send = targetType.GetMethod("Send", BindingFlags.Public | BindingFlags.Instance);
                string initial = (string)read.Invoke(target, null);
                replace.Invoke(target, new object[] { initial, "আলফ্রেড লোকাল খসড়া" });
                if ((string)read.Invoke(target, null) != "আলফ্রেড লোকাল খসড়া") throw new Exception("Unicode write/read failed");
                replace.Invoke(target, new object[] { "আলফ্রেড লোকাল খসড়া", "সংশোধিত লোকাল খসড়া" });
                if ((string)read.Invoke(target, null) != "সংশোধিত লোকাল খসড়া") throw new Exception("Draft replace/read failed");
                bool blocked = false;
                try { send.Invoke(target, new object[] { "সংশোধিত লোকাল খসড়া" }); }
                catch (TargetInvocationException ex) {
                    if (ex.InnerException != null && ex.InnerException.Message.Contains("শুধু চেনা Facebook/Messenger")) blocked = true;
                    else throw;
                }
                if (!blocked) throw new Exception("Local non-chat send was not blocked");
                result = "PASS focused UI Automation; PASS Bengali Unicode write/edit/read; PASS non-chat send blocked";
            } catch (Exception ex) { result = "FAIL " + (ex.InnerException == null ? ex.Message : ex.InnerException.Message); }
            File.WriteAllText(report, result + Environment.NewLine);
            BeginInvoke(new Action(delegate { status.Text = result; running = false; }));
        });
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    [STAThread] static void Main() {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new AlfredDraftUiProbe());
    }
}
