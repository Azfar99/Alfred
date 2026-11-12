using System;
using System.Windows.Forms;

sealed class AlfredDraftTarget : Form {
    [STAThread] static void Main() {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Form form = new Form { Text = "Alfred local draft test editor", StartPosition = FormStartPosition.CenterScreen, ClientSize = new System.Drawing.Size(620, 240) };
        TextBox editor = new TextBox { Multiline = true, AcceptsReturn = true, AccessibleName = "Local test editor", Dock = DockStyle.Fill, Font = new System.Drawing.Font("Nirmala UI", 14) };
        form.Controls.Add(editor);
        form.Shown += delegate { editor.Focus(); };
        Application.Run(form);
    }
}
