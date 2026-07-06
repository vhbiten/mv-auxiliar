using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Item da barra lateral. O texto é desenhado direto (TextRenderer), garantindo
    // linha única com reticências em qualquer DPI. Estados: hover, ativo (barra de
    // destaque à esquerda) e "desabilitado" (módulos futuros "em breve").
    public class NavButton : Panel
    {
        private readonly string _text;
        private bool _active;
        private bool _hover;
        private bool _enabledItem = true;

        // Item habilitado mostra cursor de mão; "em breve" fica inerte.
        public bool EnabledItem
        {
            get { return _enabledItem; }
            set { _enabledItem = value; this.Cursor = value ? Cursors.Hand : Cursors.Default; this.Invalidate(); }
        }

        public event EventHandler Selected;

        public NavButton(string text)
        {
            _text = text;
            this.Width = 210;
            this.Height = 48;
            this.Margin = new Padding(0);
            this.Cursor = Cursors.Hand;
            this.BackColor = Theme.Primary;
            this.DoubleBuffered = true;

            this.Click += new EventHandler(OnClick);
            this.MouseEnter += new EventHandler(OnEnter);
            this.MouseLeave += new EventHandler(OnLeave);
        }

        public bool Active
        {
            get { return _active; }
            set { _active = value; ApplyBg(); }
        }

        private void ApplyBg()
        {
            if (_active) this.BackColor = Theme.NavActive;
            else this.BackColor = _hover && EnabledItem ? Theme.Accent : Theme.Primary;
            this.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_active)
                using (var br = new SolidBrush(Color.White))
                    e.Graphics.FillRectangle(br, 0, 0, 4, this.Height); // barra de destaque

            Color cor = _active ? Color.White : (EnabledItem ? Theme.SidebarText : Theme.SidebarMuted);
            Font fnt = _active ? Theme.BodyBold : Theme.Body;
            var area = new Rectangle(24, 0, this.Width - 30, this.Height);
            TextRenderer.DrawText(e.Graphics, _text, fnt, area, cor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private void OnEnter(object s, EventArgs e) { if (!_active) { _hover = true; ApplyBg(); } }
        private void OnLeave(object s, EventArgs e) { _hover = false; ApplyBg(); }
        private void OnClick(object s, EventArgs e) { if (EnabledItem && Selected != null) Selected(this, EventArgs.Empty); }
    }
}
