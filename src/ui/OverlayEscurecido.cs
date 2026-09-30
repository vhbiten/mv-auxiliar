using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Véu escurecido atrás de um modal (profundidade). É NÃO-ATIVÁVEL e TRANSPARENTE a
    // cliques: clicar "fora do modal" cai na janela principal desabilitada e o Windows
    // faz o comportamento padrão (bipe + foco de volta no diálogo). Sem isso, o clique
    // tentava ativar o próprio véu (janela desabilitada) e a aplicação ficava sem
    // NENHUMA janela ativa — parecia travada até alternar de janela.
    internal class OverlayEscurecido : Form
    {
        public OverlayEscurecido(Form dono)
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.ControlBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.Black;
            this.Opacity = 0.32;
            this.Bounds = dono.Bounds;
            this.Enabled = false;
            this.Owner = dono;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE: nunca vira a janela ativa
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT: cliques atravessam o véu
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }
    }
}
