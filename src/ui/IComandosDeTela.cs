namespace SoulmvKit.UI
{
    // Comandos de teclado globais roteados pela MainWindow (F5, Ctrl+F) para a
    // view de módulo ativa. Implementar só o que a tela tem.
    public interface IComandosDeTela
    {
        void ComandoAtualizar();   // F5
        void ComandoFocarBusca();  // Ctrl+F
    }
}
