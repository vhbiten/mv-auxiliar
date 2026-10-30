using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Selo arredondado (pílula) com texto e, se quiser, um glifo do Segoe MDL2 à esquerda:
    // tipo/situação no topo da aba Atender, contagens e situação de cada item na conferência.
    public class Selo : Control
    {
        public static readonly Font FonteCelula = new Font("Segoe UI Semibold", 9f, FontStyle.Regular);
        private static readonly Font FonteGlifo = new Font("Segoe MDL2 Assets", 8f, FontStyle.Regular);
        private const TextFormatFlags Linha = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                                              TextFormatFlags.VerticalCenter | TextFormatFlags.Left;

        private Color _cor = Theme.TextDark, _fundo = Theme.GridHeaderBg;
        private string _glifo;

        public Selo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Font = Theme.BodyBold; BackColor = Theme.CardBg; Visible = false;
            Height = Theme.S(28);
            Margin = new Padding(Theme.S(8), Theme.S(1), 0, 0);
        }

        public void Mostrar(string texto, Color cor, Color fundo) { Mostrar(texto, null, cor, fundo); }

        public void Mostrar(string texto, string glifo, Color cor, Color fundo)
        {
            Text = texto ?? ""; _glifo = glifo; _cor = cor; _fundo = fundo;
            Width = Medir(Text, _glifo, Font).Width + Theme.S(24);
            Visible = Text.Length > 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Desenhar(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Text, _glifo, Font, _cor, _fundo);
        }

        // Largura do conteúdo (glifo + texto), sem o respiro das pontas.
        public static Size Medir(string texto, string glifo, Font fonte)
        {
            int w = TextRenderer.MeasureText(texto ?? "", fonte, Size.Empty, Linha).Width;
            if (!string.IsNullOrEmpty(glifo)) w += TextRenderer.MeasureText(glifo, FonteGlifo, Size.Empty, Linha).Width + Theme.S(6);
            return new Size(w, fonte.Height);
        }

        public static void Desenhar(Graphics g, Rectangle r, string texto, string glifo, Font fonte, Color cor, Color fundo)
        {
            var estado = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundedRect(r, r.Height / 2))
            using (var b = new SolidBrush(fundo))
                g.FillPath(b, path);
            g.Restore(estado);

            int gw = string.IsNullOrEmpty(glifo) ? 0 : TextRenderer.MeasureText(g, glifo, FonteGlifo, Size.Empty, Linha).Width;
            int gap = gw > 0 ? Theme.S(6) : 0;
            int tw = TextRenderer.MeasureText(g, texto ?? "", fonte, Size.Empty, Linha).Width;
            int x = r.X + Math.Max(Theme.S(8), (r.Width - gw - gap - tw) / 2);
            if (gw > 0)
                TextRenderer.DrawText(g, glifo, FonteGlifo, new Rectangle(x, r.Y + 1, gw + 1, r.Height), cor, Linha);
            TextRenderer.DrawText(g, texto ?? "", fonte, new Rectangle(x + gw + gap, r.Y, Math.Min(tw + 2, r.Right - x - gw - gap), r.Height), cor,
                Linha | TextFormatFlags.EndEllipsis);
        }
    }
}
