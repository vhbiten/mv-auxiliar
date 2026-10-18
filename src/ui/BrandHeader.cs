using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Faixa de marca do topo da sidebar: símbolo + "AUXILIAR" desenhados como UM bloco
    // centralizado na faixa (horizontal e vertical). Antes eram duas células de uma tabela
    // (símbolo em coluna fixa + texto alinhado à esquerda) e o par ficava 6 px fora de
    // centro — 18 px de folga à esquerda contra 31 à direita (medido em 18/10/2026).
    // A redução do símbolo é feita aqui em bicúbica de alta qualidade (o PictureBox em
    // Zoom serrilhava a borda do círculo).
    public class BrandHeader : Panel
    {
        private const string Texto = "AUXILIAR";
        private static readonly Font Fonte = new Font("Segoe UI", 14f, FontStyle.Bold);

        public BrandHeader()
        {
            this.BackColor = Color.White;
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            int lado = Theme.S(44);          // símbolo
            int vao = Theme.S(12);           // entre símbolo e texto
            Size txt = TextRenderer.MeasureText(g, Texto, Fonte, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            int largura = lado + vao + txt.Width;
            int x = (this.Width - largura) / 2;
            int y = (this.Height - lado) / 2;

            Image img = Assets.Favicon;
            if (img != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(img, new Rectangle(x, y, lado, lado));
            }

            var areaTxt = new Rectangle(x + lado + vao, 0, txt.Width + Theme.S(4), this.Height);
            TextRenderer.DrawText(g, Texto, Fonte, areaTxt, Theme.Primary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }
}
