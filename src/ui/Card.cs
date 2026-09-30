using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Cartão: painel branco de cantos retos com borda 1px sutil, sobre o fundo
    // cinza-azulado do conteúdo. Os controles filhos com BackColor=Transparent
    // mostram o branco do cartão.
    public class Card : Panel
    {
        public int Radius = 0;      // mantidos por compatibilidade; o desenho é reto
        public bool Shadow = false;

        public Card()
        {
            this.DoubleBuffered = true;
            this.BackColor = Theme.CardBg;
            // Sem isto, crescer a janela deixava a borda direita ANTIGA para trás como um
            // risco vertical nas faixas sem controle filho (topo/rodapé do cartão): o
            // Windows só repinta a região exposta, e a borda vive em Width-1.
            this.SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var br = new SolidBrush(Theme.CardBg))
                g.FillRectangle(br, this.ClientRectangle);
            using (var pen = new Pen(Theme.BorderSoft))
                g.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
        }
    }
}
