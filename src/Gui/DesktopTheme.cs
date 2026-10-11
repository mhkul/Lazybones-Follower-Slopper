using System.Runtime.InteropServices;

namespace AutoFollower.Gui;

internal static class DesktopTheme
{
    internal static readonly Color Background = Color.FromArgb(32, 34, 38), Surface = Color.FromArgb(43, 46, 51), Text = Color.FromArgb(235, 236, 239), Muted = Color.FromArgb(179, 185, 196), Accent = Color.FromArgb(72, 113, 170), Error = Color.FromArgb(255, 158, 151);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    internal static void Apply(Control control)
    {
        control.BackColor = control is TextBox or ComboBox or NumericUpDown or ListBox ? Surface : Background;
        control.ForeColor = Text;
        if (control is Button button)
        {
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Color.FromArgb(83, 89, 98);
            button.BackColor = Surface; button.UseVisualStyleBackColor = false;
        }
        if (control is DataGridView grid)
        {
            grid.BackgroundColor = Background; grid.EnableHeadersVisualStyles = false;
            grid.DefaultCellStyle.BackColor = Surface; grid.DefaultCellStyle.ForeColor = Text;
            grid.DefaultCellStyle.SelectionBackColor = Accent; grid.DefaultCellStyle.SelectionForeColor = Text;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Background; grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(38, 41, 46);
        }
        foreach (Control child in control.Controls) Apply(child);
        if (control is Form form) form.HandleCreated += (_, _) => { int enabled = 1; DwmSetWindowAttribute(form.Handle, 20, ref enabled, 4); };
    }
}

internal sealed class ReadableCheckBox : CheckBox
{
    public ReadableCheckBox() => SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var state = Checked ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal;
        Size glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
        CheckBoxRenderer.DrawCheckBox(e.Graphics, new Point(0, (Height - glyph.Height) / 2), state);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(glyph.Width + 5, 0, Width - glyph.Width - 5, Height), Enabled ? DesktopTheme.Text : DesktopTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle, DesktopTheme.Text, BackColor);
    }
}

internal sealed class ReadableButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) { base.OnPaint(e); return; }
        e.Graphics.Clear(DesktopTheme.Surface);
        using var border = new Pen(Color.FromArgb(83, 89, 98));
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, DesktopTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
