using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    public enum StatusKind { Info, Busy, Success, Warning, Error }

    // Banner de status sóbrio: fundo pastel + barra de acento 3px à esquerda +
    // glifo na cor do estado + texto em TextDark (legível, sem gritar).
    // Estado Busy anima o glifo (· ·· ···) para a espera não parecer travamento.
    public class StatusBanner : Panel
    {
        private StatusKind _kind = StatusKind.Info;
        private string _text = "";

        private readonly int PadL = Theme.S(42);   // barra de acento + glifo
        private readonly int PadR = Theme.S(14);
        private readonly int PadV = Theme.S(10);
        private readonly int MinH = Theme.S(40);

        private Timer _anim;           // só roda em Busy
        private int _animPasso;

        public StatusBanner()
        {
            this.DoubleBuffered = true;
            this.BackColor = Theme.ContentBg;
            this.AutoSize = true;
            this.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            this.Height = MinH;
            this.Margin = new Padding(0);
            this.SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public bool IsEmpty { get { return _text.Length == 0; } }

        public void SetState(StatusKind kind, string text)
        {
            _kind = kind;
            _text = text == null ? "" : text;
            Animar(_kind == StatusKind.Busy && _text.Length > 0);
            NotificarLayout();
            this.Invalidate();
        }

        public void Clear()
        {
            _text = "";
            Animar(false);
            NotificarLayout();
            this.Invalidate();
        }

        private void Animar(bool ligar)
        {
            if (ligar)
            {
                if (_anim == null)
                {
                    _anim = new Timer();
                    _anim.Interval = 350;
                    _anim.Tick += new EventHandler(delegate
                    {
                        _animPasso = (_animPasso + 1) % 3;
                        this.Invalidate(new Rectangle(0, 0, PadL, this.Height));
                    });
                }
                if (!_anim.Enabled) _anim.Start();
            }
            else if (_anim != null && _anim.Enabled)
            {
                _anim.Stop();
                _animPasso = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _anim != null) { _anim.Dispose(); _anim = null; }
            base.Dispose(disposing);
        }

        // Avisa o container que a altura preferida mudou (o layout chama GetPreferredSize).
        private void NotificarLayout()
        {
            if (this.Parent != null) this.Parent.PerformLayout(this, "PreferredSize");
        }

        // A altura é PUXADA pelo layout (AutoSize) com a largura real da célula — nunca
        // fica presa numa medição antiga feita com outra largura (ex.: antes do 1º layout).
        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width;
            if (w <= 0 || w >= int.MaxValue - 1) w = this.Width;
            if (w <= 0) w = 600;
            return new Size(w, AlturaPara(w));
        }

        private int AlturaPara(int largura)
        {
            if (_text.Length == 0) return MinH;
            int avail = largura - PadL - PadR;
            if (avail < 60) avail = 60;
            Size sz = TextRenderer.MeasureText(_text, Theme.Body, new Size(avail, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int h = sz.Height + PadV * 2;
            return h < MinH ? MinH : h;
        }

        private void Palette(out Color bg, out Color strong, out string glyph)
        {
            switch (_kind)
            {
                case StatusKind.Success: bg = Theme.SuccessBg; strong = Theme.Success; glyph = "✓"; break;
                case StatusKind.Warning: bg = Theme.WarningBg; strong = Theme.Warning; glyph = "!"; break;
                case StatusKind.Error:   bg = Theme.ErrorBg;   strong = Theme.Error;   glyph = "×"; break;
                case StatusKind.Busy:
                    bg = Theme.InfoBg; strong = Theme.Primary;
                    glyph = _animPasso == 0 ? "·" : (_animPasso == 1 ? "··" : "···");
                    break;
                default:                 bg = Theme.InfoBg;    strong = Theme.Primary; glyph = "i"; break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var br = new SolidBrush(this.Parent != null ? this.Parent.BackColor : Theme.ContentBg))
                g.FillRectangle(br, this.ClientRectangle);
            if (_text.Length == 0) return;

            Color bg, strong; string glyph;
            Palette(out bg, out strong, out glyph);

            using (var br = new SolidBrush(bg))
                g.FillRectangle(br, this.ClientRectangle);

            // barra de acento à esquerda (único enfeite)
            using (var br = new SolidBrush(strong))
                g.FillRectangle(br, 0, 0, Theme.S(3), this.Height);

            // glifo na cor do estado (sem disco)
            var ic = new Rectangle(Theme.S(11), 0, Theme.S(24), this.Height);
            TextRenderer.DrawText(g, glyph, Theme.H2, ic, strong,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // texto legível (TextDark), quebra linha
            var tr = new Rectangle(PadL, PadV, this.Width - PadL - PadR, this.Height - PadV * 2);
            TextRenderer.DrawText(g, _text, Theme.Body, tr, Theme.TextDark,
                TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
