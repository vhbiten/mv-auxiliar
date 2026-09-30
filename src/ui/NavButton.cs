using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Item da barra lateral. O texto é desenhado direto (TextRenderer), garantindo
    // linha única com reticências em qualquer DPI. Estados: hover, ativo (barra de
    // destaque à esquerda), foco por teclado (anel branco) e "em breve" (rotulado).
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
            set { _enabledItem = value; this.Cursor = value ? Cursors.Hand : Cursors.Default; this.TabStop = value; this.Invalidate(); }
        }

        public event EventHandler Selected;

        public NavButton(string text)
        {
            _text = text;
            this.Width = Theme.S(210);
            this.Height = Theme.S(48);
            this.Margin = new Padding(0);
            this.Cursor = Cursors.Hand;
            this.BackColor = Theme.Primary;
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.Selectable, true);   // alcançável por Tab
            this.TabStop = true;

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

            // O fundo é pintado INTEIRO aqui, a partir do estado — não se confia no
            // BackColor do Panel para a área toda: repaint parcial deixava risco branco
            // na borda esquerda do item ativo (bug que ia e voltava). A antiga barra de
            // destaque branca à esquerda foi removida a pedido (29/08/2026): o dono a
            // enxergava como defeito, e fundo escuro + negrito já marcam o ativo.
            Color fundo = _active ? Theme.NavActive
                        : (_hover && EnabledItem ? Theme.Accent : Theme.Primary);
            using (var br = new SolidBrush(fundo))
                e.Graphics.FillRectangle(br, this.ClientRectangle);

            Color cor = _active ? Color.White : (EnabledItem ? Theme.SidebarText : Theme.SidebarMuted);
            Font fnt = _active ? Theme.BodyBold : Theme.Body;
            var area = new Rectangle(Theme.S(24), 0, this.Width - Theme.S(30), this.Height);
            TextRenderer.DrawText(e.Graphics, _text, fnt, area, cor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // módulo futuro: além do tom apagado, um rótulo explícito
            if (!EnabledItem)
            {
                var tag = new Rectangle(Theme.S(24), 0, this.Width - Theme.S(34), this.Height);
                TextRenderer.DrawText(e.Graphics, "em breve", Theme.Small, tag, Theme.SidebarMuted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            // Anel de foco SÓ na navegação por teclado (ShowFocusCues): depois de um
            // CLIQUE o foco fica no botão e o anel branco de 1px aparecia "do nada" e
            // sumia ao clicar em outro lugar — o vai-e-volta relatado.
            if (this.Focused && this.ShowFocusCues)
            {
                using (var pen = new Pen(Color.White, 1f))
                    e.Graphics.DrawRectangle(pen, 2, 2, this.Width - 5, this.Height - 5);
            }
        }

        protected override void OnGotFocus(EventArgs e) { this.Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { this.Invalidate(); base.OnLostFocus(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (EnabledItem) this.Focus();
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && EnabledItem)
            {
                e.Handled = true;
                if (Selected != null) Selected(this, EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }

        private void OnEnter(object s, EventArgs e) { if (!_active) { _hover = true; ApplyBg(); } }
        private void OnLeave(object s, EventArgs e) { _hover = false; ApplyBg(); }
        private void OnClick(object s, EventArgs e) { if (EnabledItem && Selected != null) Selected(this, EventArgs.Empty); }
    }
}
