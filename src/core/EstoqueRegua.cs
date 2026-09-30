using System;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Régua da análise de estoque da CAF, em dias de cobertura — editável em
    // dados\estoque-regua.txt.
    //
    // Defaults CALIBRADOS contra o pedido de compras real de agosto/2026 (7 solicitações
    // do MV, 267 itens): a CAF compra em rodadas ~mensais por categoria e repõe até uma
    // cobertura mediana de ~67 dias (P25=51, P75=93). Com atenção<45 e alvo=60, a régua
    // teria apontado a grande maioria do que foi pedido; com os 30/30 antigos, só 45%.
    //   critico  = não atravessa a próxima rodada + entrega (urgência)
    //   atencao  = candidato da rodada do mês
    //   alvo     = até onde a sugestão repõe
    public static class EstoqueRegua
    {
        private const int CriticoPadrao = 15;
        private const int AtencaoPadrao = 45;
        private const int AlvoPadrao = 60;

        private static readonly object _lock = new object();
        private static int _critico = CriticoPadrao;
        private static int _atencao = AtencaoPadrao;
        private static int _alvo = AlvoPadrao;

        public static int CriticoDias { get { lock (_lock) return _critico; } }
        public static int AtencaoDias { get { lock (_lock) return _atencao; } }
        public static int AlvoDias { get { lock (_lock) return _alvo; } }

        public static void Init(string dirBase)
        {
            lock (_lock)
            {
                try
                {
                    string dir = Path.Combine(dirBase, "dados");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "estoque-regua.txt");
                    if (!File.Exists(path)) { CriarModelo(path); return; }

                    foreach (var linha in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string s = (linha ?? "").Trim();
                        if (s.Length == 0 || s.StartsWith("#")) continue;
                        int cerq = s.IndexOf('#');
                        if (cerq >= 0) s = s.Substring(0, cerq).Trim();
                        int ig = s.IndexOf('=');
                        if (ig <= 0) continue;
                        string chave = s.Substring(0, ig).Trim().ToLowerInvariant();
                        int v;
                        if (!int.TryParse(s.Substring(ig + 1).Trim(), out v)) continue;
                        if (v < 1) v = 1; if (v > 365) v = 365;   // sanidade
                        if (chave == "critico") _critico = v;
                        else if (chave == "atencao") _atencao = v;
                        else if (chave == "alvo") _alvo = v;
                    }
                    // Ordem tem que fazer sentido: crítico <= atenção <= alvo.
                    if (_atencao < _critico) _atencao = _critico;
                    if (_alvo < _atencao) _alvo = _atencao;
                    Logger.Log("EstoqueRegua: critico=" + _critico + " atencao=" + _atencao + " alvo=" + _alvo + " dias.");
                }
                catch (Exception ex)
                {
                    Logger.Log("EstoqueRegua: falha ao carregar (" + ex.Message + ") — usando os padrões.");
                    _critico = CriticoPadrao; _atencao = AtencaoPadrao; _alvo = AlvoPadrao;
                }
            }
        }

        private static void CriarModelo(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Regua da analise de Estoque (CAF), em DIAS de cobertura.");
            sb.AppendLine("#");
            sb.AppendLine("# critico = item nao atravessa a proxima rodada de compra + prazo de entrega");
            sb.AppendLine("# atencao = candidato a entrar na rodada do mes");
            sb.AppendLine("# alvo    = ate onde a sugestao de compra repoe o estoque");
            sb.AppendLine("#");
            sb.AppendLine("# Padroes calibrados contra o pedido real de agosto/2026 (a CAF repoe ate");
            sb.AppendLine("# uma cobertura mediana de ~67 dias, comprando em rodadas mensais por tipo).");
            sb.AppendLine("#");
            sb.AppendLine("# Depois de editar, feche e abra o programa e gere a analise de novo.");
            sb.AppendLine();
            sb.AppendLine("critico=" + CriticoPadrao);
            sb.AppendLine("atencao=" + AtencaoPadrao);
            sb.AppendLine("alvo=" + AlvoPadrao);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
