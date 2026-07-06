using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    public enum StatusKind { Info, Busy, Success, Warning, Error }

    // Banner de status arredondado: barra de acento + ícone à esquerda e texto
    // que quebra linha. Altura calculada conforme o texto. Pintado à mão (leve).
    public class StatusBanner : Panel
    {
        private StatusKind _kind = StatusKind.Info;
        private string _text = "";

        private const int PadL = 50;   // espaço para barra de acento + ícone
        private const int PadR = 16;
        private const int PadV = 11;
        private const int MinH = 48;

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
            NotificarLayout();
            this.Invalidate();
        }

        public void Clear()
        {
            _text = "";
            NotificarLayout();
            this.Invalidate();
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
                case StatusKind.Success: bg = Theme.SuccessBg; strong = Theme.Success; glyph = "✓"; break; // check
                case StatusKind.Warning: bg = Theme.WarningBg; strong = Theme.Warning; glyph = "!";        break;
                case StatusKind.Error:   bg = Theme.ErrorBg;   strong = Theme.Error;   glyph = "×";   break; // ×
                case StatusKind.Busy:    bg = Theme.InfoBg;    strong = Theme.Primary; glyph = "…";   break; // …
                default:                 bg = Theme.InfoBg;    strong = Theme.Primary; glyph = "i";        break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(this.Parent != null ? this.Parent.BackColor : Theme.ContentBg);
            if (_text.Length == 0) return;

            Color bg, strong; string glyph;
            Palette(out bg, out strong, out glyph);

            var r = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            using (var path = Theme.RoundedRect(r, 8))
            using (var br = new SolidBrush(bg))
            using (var pen = new Pen(Color.FromArgb(70, strong)))
            {
                g.FillPath(br, path);
                g.DrawPath(pen, path);
            }

            // barra de acento à esquerda
            using (var br = new SolidBrush(strong))
                g.FillRectangle(br, 0, 7, 4, this.Height - 14);

            // ícone em disco
            var ic = new Rectangle(16, (this.Height - 22) / 2, 22, 22);
            using (var br = new SolidBrush(strong))
                g.FillEllipse(br, ic);
            TextRenderer.DrawText(g, glyph, Theme.BodyBold, ic, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // texto (quebra linha, alinhado verticalmente)
            var tr = new Rectangle(PadL, PadV, this.Width - PadL - PadR, this.Height - PadV * 2);
            TextRenderer.DrawText(g, _text, Theme.Body, tr, strong,
                TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
