using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Um tipo de contagem do CAF: pertence a uma semana (1..5) e tem a lista de códigos.
    public class TipoContagemCaf
    {
        public int Semana;
        public string Nome;
        public List<int> Codigos = new List<int>();
    }

    // Escala de contagem do CAF (Estoque 2): o estoque físico é contado por SEMANA
    // (1..5, semana do mês) e, dentro de cada semana, por TIPO de material/medicamento
    // (alta vigilância, comprimidos, soros, equipos...). Diferente da Farmácia Central,
    // estas listas são EDITÁVEIS: ficam em dados\contagem-caf.txt (criado na primeira
    // edição a partir da semente embutida, extraída da escala oficial em jul/2026).
    public static class ContagemCaf
    {
        public const int Estoque = 2;

        private static readonly object _lock = new object();
        private static List<TipoContagemCaf> _tipos = new List<TipoContagemCaf>();
        private static string _path;

        // Carrega o arquivo editável (se existir) ou a semente (chamar uma vez, no início).
        public static void Init(string dirBase)
        {
            lock (_lock)
            {
                try
                {
                    string dirDados = Path.Combine(dirBase, "dados");
                    Directory.CreateDirectory(dirDados);
                    _path = Path.Combine(dirDados, "contagem-caf.txt");

                    if (File.Exists(_path))
                        _tipos = Interpretar(File.ReadAllText(_path, Encoding.UTF8));
                    else
                        _tipos = Interpretar(Semente);
                }
                catch (Exception ex)
                {
                    Logger.Log("ContagemCaf: falha ao carregar (" + ex.Message + ") — usando a semente.");
                    _tipos = Interpretar(Semente);
                }
            }
        }

        // Tipos de uma semana, na ordem do arquivo (cópias — edite via Salvar/Excluir).
        public static List<TipoContagemCaf> TiposDaSemana(int semana)
        {
            lock (_lock)
            {
                var r = new List<TipoContagemCaf>();
                foreach (var t in _tipos)
                    if (t.Semana == semana) r.Add(Clonar(t));
                return r;
            }
        }

        // Já existe um tipo com esse nome nessa semana? (ignora o próprio nomeOriginal)
        public static bool NomeEmUso(int semana, string nome, string nomeOriginal)
        {
            lock (_lock)
            {
                foreach (var t in _tipos)
                {
                    if (t.Semana != semana) continue;
                    if (nomeOriginal != null && Igual(t.Nome, nomeOriginal)) continue;
                    if (Igual(t.Nome, nome)) return true;
                }
                return false;
            }
        }

        // Cria (nomeOriginal=null) ou atualiza (localiza por semana+nomeOriginal) um tipo.
        public static bool Salvar(int semana, string nomeOriginal, string nome, List<int> codigos)
        {
            lock (_lock)
            {
                if (nomeOriginal == null)
                {
                    var novo = new TipoContagemCaf();
                    novo.Semana = semana; novo.Nome = nome; novo.Codigos = new List<int>(codigos);
                    _tipos.Add(novo);
                    if (Persistir()) return true;
                    _tipos.Remove(novo);   // rollback: não deixa fantasma na memória
                    return false;
                }
                foreach (var t in _tipos)
                {
                    if (t.Semana == semana && Igual(t.Nome, nomeOriginal))
                    {
                        string nomeAntes = t.Nome; var codigosAntes = t.Codigos;
                        t.Nome = nome;
                        t.Codigos = new List<int>(codigos);
                        if (Persistir()) return true;
                        t.Nome = nomeAntes; t.Codigos = codigosAntes;   // rollback
                        return false;
                    }
                }
                return false;
            }
        }

        public static bool Excluir(int semana, string nome)
        {
            lock (_lock)
            {
                for (int i = 0; i < _tipos.Count; i++)
                {
                    if (_tipos[i].Semana == semana && Igual(_tipos[i].Nome, nome))
                    {
                        var removido = _tipos[i];
                        _tipos.RemoveAt(i);
                        if (Persistir()) return true;
                        _tipos.Insert(i, removido);   // rollback
                        return false;
                    }
                }
                return false;
            }
        }

        // Extrai os códigos de um texto livre (delega ao helper compartilhado).
        public static List<int> ParseCodigos(string texto, out int ignoradas)
        {
            return ContagemCodigos.Parse(texto, out ignoradas);
        }

        // Códigos da SEMENTE de um tipo (para "Restaurar padrão"); null se o tipo não
        // faz parte da escala oficial (foi criado pelo usuário).
        public static List<int> PadraoDoTipo(int semana, string nome)
        {
            foreach (var t in Interpretar(Semente))
                if (t.Semana == semana && Igual(t.Nome, nome)) return new List<int>(t.Codigos);
            return null;
        }

        // ===== interno =====

        private static bool Igual(string a, string b)
        {
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static TipoContagemCaf Clonar(TipoContagemCaf t)
        {
            var c = new TipoContagemCaf();
            c.Semana = t.Semana; c.Nome = t.Nome; c.Codigos = new List<int>(t.Codigos);
            return c;
        }

        // Formato do arquivo: "[semana N] NOME DO TIPO" abre um tipo; abaixo, códigos
        // (um por linha ou separados por vírgula; o que vem após "-" é ignorado).
        private static List<TipoContagemCaf> Interpretar(string texto)
        {
            var tipos = new List<TipoContagemCaf>();
            TipoContagemCaf atual = null;
            foreach (var linha in texto.Split(new char[] { '\n' }))
            {
                string ln = linha.Trim().TrimEnd('\r');
                if (ln.Length == 0 || ln.StartsWith("#")) continue;

                if (ln.StartsWith("[semana", StringComparison.OrdinalIgnoreCase))
                {
                    // Cabeçalho inválido zera 'atual' — senão os códigos da seção órfã
                    // seriam anexados ao tipo anterior.
                    int fecha = ln.IndexOf(']');
                    if (fecha < 0) { atual = null; continue; }
                    int sem;
                    if (!int.TryParse(ln.Substring(7, fecha - 7).Trim(), out sem)) { atual = null; continue; }
                    if (sem < 1 || sem > 5) { atual = null; continue; }
                    string nomeTipo = ln.Substring(fecha + 1).Trim();
                    if (nomeTipo.Length == 0) { atual = null; continue; }
                    // Mescla seções duplicadas (semana+nome) de um arquivo editado à mão,
                    // para garantir no máximo um registro por nome — senão Salvar/Excluir,
                    // que agem no primeiro match, atingiriam o tipo errado.
                    atual = null;
                    foreach (var ex in tipos)
                        if (ex.Semana == sem && Igual(ex.Nome, nomeTipo)) { atual = ex; break; }
                    if (atual == null)
                    {
                        atual = new TipoContagemCaf();
                        atual.Semana = sem;
                        atual.Nome = nomeTipo;
                        tipos.Add(atual);
                    }
                    continue;
                }

                if (atual == null) continue;
                int ign;
                foreach (int cd in ContagemCodigos.Parse(ln, out ign))
                    if (!atual.Codigos.Contains(cd)) atual.Codigos.Add(cd);
            }
            return tipos;
        }

        private static bool Persistir()
        {
            if (_path == null) return false;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Escala de contagem do CAF (Estoque 2) — editada pelo Auxiliar MV Soul.");
                sb.AppendLine("# Formato: \"[semana N] NOME DO TIPO\" abre um tipo; abaixo, um código por");
                sb.AppendLine("# linha (o que vier depois de \"-\" é só o nome do produto, ignorado).");
                for (int s = 1; s <= 5; s++)
                {
                    foreach (var t in _tipos)
                    {
                        if (t.Semana != s) continue;
                        sb.AppendLine();
                        sb.AppendLine("[semana " + t.Semana + "] " + t.Nome);
                        foreach (int cd in t.Codigos)
                            sb.AppendLine(ContagemCodigos.LinhaComNome(cd));
                    }
                }
                return ContagemCodigos.GravarAtomico(_path, sb.ToString());
            }
            catch (Exception ex)
            {
                Logger.Log("ContagemCaf: falha ao gravar " + _path + ": " + ex.Message);
                return false;
            }
        }

        // Semente: extraída de "ESCALA DE CONTAGEM DIÁRIA CAF.docx" (jul/2026), na
        // organização das tabelas de códigos do documento (coluna esquerda, depois direita).
        private const string Semente =
            "[semana 1] ANTIBIÓTICOS\n" +
            "9000744,9017228,15996,9004024,13583,9010480,9013148,13497,9026323,9023432,\n" +
            "9005384,9019752,9017463,9032052,9009394,9013638,9013096,9032984,9032382,9011071,\n" +
            "15616,9011684,9026283,13577,9027155,9018717,9015674,9002637,9016630,9018620,\n" +
            "9021041\n" +

            "[semana 1] ALTA VIGILÂNCIA\n" +
            "9000792,9003100,9004047,9002192,9022691,13612,9021989,13553,9005279,13538,\n" +
            "9004580,9005580,9023680,9008383,9008236,9004361,9004364,9004367,9004369,9030239,\n" +
            "9026601,13593,9001769,14077,9010042,14094,9022069,14084,14083,16229,\n" +
            "9004529,9017831,9020717,9017860,9026567,9017553,14023,9018351,9011710,13626,\n" +
            "9014146,13623,13591,13502,13611,9004772,9000717,13587\n" +

            "[semana 1] COMPRIMIDOS\n" +
            "9001121,9025546,16524,9016361,9033115,9017612,9035809,9000360,13528,9008626,\n" +
            "9002583,9008304,9008306,13538,9014499,9007980,9035672,9012925,9038007,9017985,\n" +
            "9027778,9007353,13564,16559,9020240,13570,9000946,9012964,9013497,13573,\n" +
            "9009037,9005189,9013132,9001153,9022139,9001358,9025709,14299,9017661,9026806,\n" +
            "9035657,13586,13588,16134,9024861,9018883,9020725\n" +

            "[semana 1] SOROS\n" +
            "13498,9000796,13599,9005585,9005655,9005650,13868,16694,9005648,13601,\n" +
            "14489,13596,14466,13598,16693\n" +

            "[semana 1] AMPOLAS E FRASCOS\n" +
            "14095,9000902,13514,13515,9000791,9020030,9015348,9002845,9023909,9026443,\n" +
            "9004492,9024016,13890,9008029,9012954,9008853,14183,9003189,13531,13202,\n" +
            "9022059,9022154,9026584,15882,9010811,15841,9026252,9026451,13850,15902,\n" +
            "13930,9017508,9039848,9003536,9004267,9014146,13606,9017279,16032,9015424,\n" +
            "13529,13613\n" +

            "[semana 2] EQUIPOS\n" +
            "13715,13716,8984,11137,10140,13717,6758,6755,13718,13800,7182,6757\n" +

            "[semana 2] MATERIAIS DE USO COMPARTILHADO\n" +
            "13185,13723,13665,13666,12255,3624,13680,13673,13880,13870,\n" +
            "13689,13691,13690,16754,13776,16054,16780,13694,13695,5938,\n" +
            "5835,13696,13750,13698,13550,13708,13722,8918,9661,5738,\n" +
            "13751,13767,13677,13681,13768,13769,13770,13779,13780,13781,\n" +
            "13775,13777,16781,14029,13782,13693,14552,13783,13785,16691,\n" +
            "13803,13835,4901,13669\n" +

            "[semana 3] SONDAS E DRENOS\n" +
            "13812,13816,13813,13814,13815,11199,11113,10688,11561,11560,\n" +
            "12532,12533,12534,12535,12536,12537,12538,12566,6390,13714,\n" +
            "6391,13712,6392,13713,5390,14030,12583,12584,12567,12568,\n" +
            "12569,13817,13822,9426,13823,13824,13825,13826,13827,13828,\n" +
            "13819,5478,6376,6388,6389,16744,12782,5589,5505,12519\n" +

            "[semana 3] TUBOS, CÂNULAS E COLETORES\n" +
            "13642,13643,13429,13644,12771,14767,12772,12773,12775,15889,\n" +
            "15874,14771,14772,14773,14775,12459,12466,12467,12468,12469,\n" +
            "12470,14778,14779,14777,14780,14782,10189,10858,10631,10634,\n" +
            "14784,13692,13479,13482,14768,14769,10025,10026,8374,14013,\n" +
            "13672,12458,12465,12471,13641,13481,14012,13480,15781,14457,\n" +
            "14776,13461,13462,10857,10635,10632,10636\n" +

            "[semana 4] MATERIAIS DE USO PACIENTE\n" +
            "13504,13505,13506,13507,13508,13509,5612,5606,5607,13633,\n" +
            "13513,5794,5896,5457,5467,5579,5581,13907,5582,5583,\n" +
            "14842,5674,11201,10610,14770,13724,13725,13734,13726,13891,\n" +
            "9062,13804,11429,8707,13727,13802\n" +

            "[semana 4] IMAGEM E PEQUENAS CIRURGIAS\n" +
            "5294,10845,5203,14062,13636,13638,2927,5296,10452,14196,\n" +
            "13874,13873,13512,12238,5008,13687,13478,13637,16061,13648,\n" +
            "14727,16058,14026,13761,14021,14020,13650,13759,16576,6086,\n" +
            "6089,6091,5244,13778,13771,13772,13773,13774,14485,5292,\n" +
            "16722,14649\n" +

            "[semana 4] AGULHAS E SERINGAS\n" +
            "4976,4979,4983,4988,4990,4992,4993,13875,11149,10478,\n" +
            "11150,14063,12090,12092,12084,12091,12093,12086,12088,16703,\n" +
            "14119,15297,16700\n" +

            "[semana 5] MULTIDOSE E POMADAS\n" +
            "16223,9003720,9008628,13543,9026185,14090,13554,13574,13546,9009165,\n" +
            "9023488,9017767,9004475,9026277,9030088,9026283,9008219,13198,14091,14093,\n" +
            "9017656,14142,13921,13590,14086,13602,9017352,16133,13701,13551,\n" +
            "9012454,16605\n" +

            "[semana 5] CURATIVOS E CATETERES\n" +
            "6119,13855,6120,13856,13878,13889,13720,13656,13705,13706,\n" +
            "13658,13659,13749,16753,13660,14465,13654,13860,15319,13859,\n" +
            "13707,13704,16206,16675,16690,14770,14893,16723\n";
    }
}
