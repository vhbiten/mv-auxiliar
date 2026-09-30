using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // Portão ÚNICO de conversa com o MV.
    //
    // O servidor de forms não tolera duas conversas simultâneas na MESMA sessão: validado ao
    // vivo, paralelizar consultas perdia registros e dava TaskCanceledException. Antes disso não
    // ser um problema porque só o usuário disparava operações (uma de cada vez); com o monitor
    // de solicitações varrendo em segundo plano a cada 30s, qualquer tela pode ser pega no meio.
    //
    // Toda tela que fala com o MV entra por aqui. Roda só na thread da UI, então não precisa de
    // lock: a troca de contexto acontece nos awaits, nunca no meio de Entrar().
    public static class MvGate
    {
        private static bool _ocupado;
        private static string _quem;

        public static bool Ocupado { get { return _ocupado; } }
        public static string Quem { get { return _quem; } }

        public static bool Entrar(string quem)
        {
            if (_ocupado) return false;
            _ocupado = true; _quem = quem;
            return true;
        }

        public static void Sair() { _ocupado = false; _quem = null; }

        // Espera a vez por até msMax milissegundos. false = não conseguiu (avise o usuário e
        // NÃO chame o MV assim mesmo — é exatamente o caso que corrompe a consulta do outro).
        public static async Task<bool> EntrarAsync(string quem, int msMax)
        {
            for (int i = 0; i < msMax / 100 && _ocupado; i++) await Task.Delay(100);
            return Entrar(quem);
        }
    }
}
