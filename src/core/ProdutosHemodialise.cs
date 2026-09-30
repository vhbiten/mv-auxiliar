using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Quem é HEMODIÁLISE no pré-pedido.
    //
    // Aqui NÃO dá para usar regra por nome: a lista tem desde "SOLUCAO ACIDA HEMODIALISE" até
    // "DIALISADOR 21H" e "KIT LINHA DE SANGUE VENOSA E ARTERIAL", que não têm palavra em comum.
    // É uma lista fechada, informada pela farmácia, e editável em dados/hemodialise.txt.
    //
    // Detalhe que manda no fluxo: esses produtos ATRAVESSAM AS DUAS ESPÉCIES do MV — 16693/16694
    // são espécie 1 (medicamentos) e 16690/16691 são espécie 2 (materiais). Por isso o pedido de
    // hemodiálise gera os DOIS relatórios de movimentação, e não um só como os outros tipos.
    public static class ProdutosHemodialise
    {
        private static string _caminho;
        private static readonly HashSet<int> _codigos = new HashSet<int>();

        // Lista informada pela farmácia (17/08/2026): dialisador, kit de linha de sangue, as
        // duas soluções (ácida e básica) e o isolador de pressão/transdutor.
        private static readonly int[] Semente = { 16690, 16691, 16693, 16694, 16723 };

        public static void Init(string baseDir)
        {
            string dir = Path.Combine(baseDir, "dados");
            Directory.CreateDirectory(dir);
            _caminho = Path.Combine(dir, "hemodialise.txt");
            _codigos.Clear();
            foreach (int c in Semente) _codigos.Add(c);

            try
            {
                if (!File.Exists(_caminho)) { CriarModelo(); return; }
                foreach (var linha in File.ReadAllLines(_caminho))
                {
                    string s = linha.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    int cerq = s.IndexOf('#');
                    if (cerq >= 0) s = s.Substring(0, cerq).Trim();
                    if (s.Length == 0) continue;
                    bool tirar = s.StartsWith("-");
                    if (tirar) s = s.Substring(1).Trim();
                    int cod;
                    if (!int.TryParse(s, out cod) || cod <= 0) continue;
                    if (tirar) _codigos.Remove(cod); else _codigos.Add(cod);
                }
                Logger.Log("Hemodiálise: " + _codigos.Count + " produtos (arquivo " + _caminho + ").");
            }
            catch (Exception ex) { Logger.Log("Hemodiálise: falha ao ler " + _caminho + ": " + ex.Message); }
        }

        private static void CriarModelo()
        {
            try
            {
                // Sem acentos, como os demais arquivos de dados do app.
                var sb = new StringBuilder();
                sb.AppendLine("# Produtos do pedido de HEMODIALISE (tipo \"Hemodialise\" na Previsao de Consumo).");
                sb.AppendLine("# Eles saem dos pedidos de medicamentos, soros e materiais.");
                sb.AppendLine("#");
                sb.AppendLine("# Um codigo por linha. Para tirar um da lista, use o sinal de menos:");
                sb.AppendLine("#   16800     -> passa a fazer parte do pedido de hemodialise");
                sb.AppendLine("#   -16723    -> sai do pedido de hemodialise");
                sb.AppendLine("#");
                sb.AppendLine("# Depois de editar, feche e abra o programa.");
                sb.AppendLine();
                foreach (int c in Semente) sb.AppendLine(c.ToString());
                File.WriteAllText(_caminho, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Logger.Log("Hemodiálise: não consegui criar " + _caminho + ": " + ex.Message); }
        }

        public static bool EhHemodialise(int codigo) { return _codigos.Contains(codigo); }

        public static List<int> Codigos()
        {
            var l = new List<int>(_codigos);
            l.Sort();
            return l;
        }
    }
}
