using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    public class SetorComparacao
    {
        public int Codigo;
        public string Nome;
    }

    // Setores que aparecem como colunas de comparação na análise de estoque da CAF
    // ("a CAF está baixa, mas quanto tem na Central? E no Centro Cirúrgico?").
    // Editável em dados\estoque-setores.txt; a semente são as 4 farmácias com estoque
    // relevante (escolha do farmacêutico em ago/2026). Os códigos são os do cadastro
    // de estoques do MV (LOV do CD_ESTOQUE).
    public static class EstoqueSetores
    {
        private static readonly object _lock = new object();
        private static List<SetorComparacao> _setores = new List<SetorComparacao>();
        private static string _path;

        public static void Init(string dirBase)
        {
            lock (_lock)
            {
                try
                {
                    string dirDados = Path.Combine(dirBase, "dados");
                    Directory.CreateDirectory(dirDados);
                    _path = Path.Combine(dirDados, "estoque-setores.txt");
                    if (!File.Exists(_path)) CriarModelo();
                    else MigrarNomeCc();
                    _setores = Interpretar(File.ReadAllLines(_path, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    Logger.Log("EstoqueSetores: falha ao carregar (" + ex.Message + ") — usando a semente.");
                    _setores = Interpretar(Semente.Split('\n'));
                }
                if (_setores.Count == 0) _setores = Interpretar(Semente.Split('\n'));
            }
        }

        // Cópia da lista atual (ordem do arquivo).
        public static List<SetorComparacao> Listar()
        {
            lock (_lock)
            {
                var r = new List<SetorComparacao>();
                foreach (var s in _setores)
                {
                    var c = new SetorComparacao();
                    c.Codigo = s.Codigo; c.Nome = s.Nome;
                    r.Add(c);
                }
                return r;
            }
        }

        private static List<SetorComparacao> Interpretar(string[] linhas)
        {
            var r = new List<SetorComparacao>();
            var vistos = new HashSet<int>();
            foreach (var linha in linhas)
            {
                string s = (linha ?? "").Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                int cerq = s.IndexOf('#');
                if (cerq >= 0) s = s.Substring(0, cerq).Trim();
                int ig = s.IndexOf('=');
                if (ig <= 0) continue;
                int cod;
                if (!int.TryParse(s.Substring(0, ig).Trim(), out cod) || cod <= 0) continue;
                string nome = s.Substring(ig + 1).Trim();
                if (nome.Length == 0 || !vistos.Add(cod)) continue;
                var sc = new SetorComparacao();
                sc.Codigo = cod; sc.Nome = nome;
                r.Add(sc);
            }
            return r;
        }

        // Nomes curtos de propósito: viram cabeçalho de coluna na grade (≈60 px cada).
        private const string Semente =
            "5=Central\n17=CC\n23=UTI\n20=Quimio\n";

        // O farmacêutico pediu "CC" no lugar de "C.Cir." (23/08/2026). O arquivo já existe
        // nas máquinas com o nome antigo da semente — troca SÓ a linha exata "17=C.Cir."
        // (nome editado à mão pelo usuário não é tocado).
        private static void MigrarNomeCc()
        {
            try
            {
                var linhas = File.ReadAllLines(_path, Encoding.UTF8);
                bool mudou = false;
                for (int i = 0; i < linhas.Length; i++)
                    if (linhas[i].Trim() == "17=C.Cir.") { linhas[i] = "17=CC"; mudou = true; }
                if (mudou)
                {
                    File.WriteAllLines(_path, linhas, new UTF8Encoding(false));
                    Logger.Log("EstoqueSetores: setor 17 renomeado de \"C.Cir.\" para \"CC\".");
                }
            }
            catch (Exception ex) { Logger.Log("EstoqueSetores: migração do nome falhou: " + ex.Message); }
        }

        private static void CriarModelo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Setores comparados na analise de Estoque (CAF): codigo do estoque no MV = nome curto.");
            sb.AppendLine("# A ordem daqui e a ordem das colunas. Cada setor custa ~4s a mais na analise e uma");
            sb.AppendLine("# coluna a mais na grade. Sao 4 setores no maximo: o quinto joga a grade para a");
            sb.AppendLine("# rolagem lateral na janela minima.");
            sb.AppendLine("#");
            sb.AppendLine("# Use nome CURTO, de ate 7 letras: Central e Quimio ja ocupam a coluna inteira, e");
            sb.AppendLine("# nome maior sai cortado no cabecalho.");
            sb.AppendLine("#");
            sb.AppendLine("# Outros codigos uteis (cadastro do MV): 1=Almoxarifado Central, 2=CAF (ja e a base da");
            sb.AppendLine("# analise), 16=Agencia Transfusional, 21=Judicializados, 46=PAM.");
            sb.AppendLine("#");
            sb.AppendLine("# Depois de editar, feche e abra o programa.");
            sb.AppendLine();
            sb.Append(Semente);
            File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
