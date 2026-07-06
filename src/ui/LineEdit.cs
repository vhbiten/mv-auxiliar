using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Campo de texto com sublinhado que acende em azul ao focar (feedback claro).
    public class LineEdit : Panel
    {
        private readonly TextBox _tb;
        private bool _focado;

        public LineEdit()
        {
            this.Height = 36;
            this.BackColor = Color.White;
            this.DoubleBuffered = true;
            this.Padding = new Padding(2, 8, 2, 0);
            this.TabStop = false;   // o tab vai direto para o TextBox interno

            _tb = new TextBox();
            _tb.BorderStyle = BorderStyle.None;
            _tb.Font = Theme.Input;
            _tb.Dock = DockStyle.Top;
            _tb.BackColor = Color.White;
            _tb.ForeColor = Theme.TextDark;
            _tb.GotFocus += new EventHandler(delegate { _focado = true; this.Invalidate(); });
            _tb.LostFocus += new EventHandler(delegate { _focado = false; this.Invalidate(); });
            this.Controls.Add(_tb);
        }

        public TextBox Inner { get { return _tb; } }

        public override string Text
        {
            get { return _tb.Text; }
            set { _tb.Text = value; }
        }

        public bool Password { set { _tb.UseSystemPasswordChar = value; } }
        public CharacterCasing Casing { set { _tb.CharacterCasing = value; } }

        public void FocusInput() { _tb.Focus(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int h = _focado ? 2 : 1;
            Color c = _focado ? Theme.Primary : Theme.Border;
            using (var b = new SolidBrush(c))
                e.Graphics.FillRectangle(b, 0, this.Height - h, this.Width, h);
        }
    }
}
