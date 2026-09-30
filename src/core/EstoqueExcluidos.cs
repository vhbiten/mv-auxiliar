using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Itens que ficam FORA da análise de estoque da CAF, em três camadas:
    //
    //  1. flag "não padronizado" do CADASTRO do MV — filtrada na origem, pelos checkboxes
    //     dos próprios relatórios (SoPadronizadosAsync). Hoje pega pouca coisa: o hospital
    //     quase não usa o flag;
    //  2. marcador "(NÃO PADRÃO)" no NOME do produto — a convenção que o hospital usa de
    //     verdade. Detectado aqui, normalizado (sem acento). Limite conhecido: nome longo
    //     truncado pelo relatório pode cortar o marcador — daí a camada 3;
    //  3. lista manual em dados\estoque-excluidos.txt — itens comprados por paciente
    //     específico (sondas/cânulas Biesalski, por tamanho) e o que mais o farmacêutico
    //     decidir tirar. Editável; semente com os casos conhecidos.
    public static class EstoqueExcluidos
    {
        private static readonly object _lock = new object();
        private static HashSet<int> _codigos = new HashSet<int>();
        private static string _path;

        public static void Init(string dirBase)
        {
            lock (_lock)
            {
                try
                {
                    string dirDados = Path.Combine(dirBase, "dados");
                    Directory.CreateDirectory(dirDados);
                    _path = Path.Combine(dirDados, "estoque-excluidos.txt");
                    if (!File.Exists(_path)) CriarModelo();
                    _codigos = Interpretar(File.ReadAllLines(_path, Encoding.UTF8));
                    Logger.Log("EstoqueExcluidos: " + _codigos.Count + " código(s) na lista.");
                }
                catch (Exception ex)
                {
                    Logger.Log("EstoqueExcluidos: falha ao carregar (" + ex.Message + ") — usando a semente.");
                    _codigos = Interpretar(Semente.Split('\n'));
                }
            }
        }

        // O item fica fora da análise? (lista manual OU marcador de não-padrão no nome)
        public static bool Excluido(int codigo, string nome)
        {
            lock (_lock) { if (_codigos.Contains(codigo)) return true; }
            return TemMarcadorNaoPadrao(nome);
        }

        // "(NÃO PADRÃO)", "NAO PADRAO", "(NÃO PADRONIZADO)"... — comparação sem acento.
        public static bool TemMarcadorNaoPadrao(string nome)
        {
            if (string.IsNullOrEmpty(nome)) return false;
            return SemAcento(nome).IndexOf("NAO PADR", StringComparison.Ordinal) >= 0;
        }

        private static string SemAcento(string s)
        {
            string d = s.ToUpperInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString();
        }

        private static HashSet<int> Interpretar(string[] linhas)
        {
            var r = new HashSet<int>();
            foreach (var linha in linhas)
            {
                string s = (linha ?? "").Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                int cerq = s.IndexOf('#');
                if (cerq >= 0) s = s.Substring(0, cerq).Trim();
                int cod;
                if (int.TryParse(s, out cod) && cod > 0) r.Add(cod);
            }
            return r;
        }

        private const string Semente =
            "13685  # CANULA DE TRAQUEOSTOMIA BIESALSKI 7,0 (compra por paciente)\n" +
            "14341  # CANULA DE TRAQUEOSTOMIA BIESALKI 7,0 (compra por paciente)\n";

        private static void CriarModelo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Itens que FICAM FORA da analise de Estoque (CAF): um codigo por linha.");
            sb.AppendLine("# Use para itens comprados por paciente especifico (sondas/canulas por tamanho,");
            sb.AppendLine("# judicializados etc.) e para qualquer item que nao deva entrar na lista de compra.");
            sb.AppendLine("#");
            sb.AppendLine("# O que vem depois de # e comentario. Itens com \"(NAO PADRAO)\" no nome ja saem");
            sb.AppendLine("# sozinhos, sem precisar de linha aqui.");
            sb.AppendLine("#");
            sb.AppendLine("# Depois de editar, feche e abra o programa (ou gere a analise de novo).");
            sb.AppendLine();
            sb.Append(Semente);
            File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
