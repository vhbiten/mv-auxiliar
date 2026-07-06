using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Nome de exibição dos produtos (código -> descrição), para listas offline.
    // Semente embutida (extraída dos PDFs de conferência e HARs reais, jun-jul/2026)
    // + cache local "nomes-produtos.txt" ao lado do exe: o app APRENDE nomes novos
    // toda vez que uma conferência é gerada na LAN (lê o PDF do relatório) e persiste.
    public static class ProdutoNomes
    {
        private static readonly object _lock = new object();
        private static Dictionary<int, string> _nomes = new Dictionary<int, string>();
        private static string _path;

        private static readonly Dictionary<int, string> _semente = new Dictionary<int, string>
        {
            { 4983, "AGULHA DESCARTAVEL 25X7 - 70014280" },
            { 4992, "AGULHA DESCARTAVEL 40X12" },
            { 5835, "COMPRESSA GAZE 7,5CMX7,5CM 13 FIOS ESTERIL 10UNID" },
            { 6120, "CATETER VENOSO CENTRAL TRIPLO LUMEN" },
            { 10140, "EQUIPO MACROGOTAS C/INJ. LATERAL" },
            { 10610, "ELETRODO DESCARTAVEL PARA ELETROCIRURGIA" },
            { 10676, "FIO NYLON 3-0 PRETO 45CM C/AG 3.0CM" },
            { 11429, "TORNEIRA 3 VIAS LUER LOCK" },
            { 12091, "SERINGA DESCARTAVEL 10 ML SLIP" },
            { 12093, "SERINGA DESCARTAVEL 20 ML SLIP" },
            { 13455, "KIT ACESSO VENOSO CENTRAL TRIPLO LUMEN" },
            { 13478, "ESCOVA DE CLOREXIDINA 2%" },
            { 13479, "COLETOR DE URINA SISTEMA FECHADO 2000ML" },
            { 13488, "KIT ACESSO VENOSO CENTRAL DUPLO LUMEN" },
            { 13489, "KIT ACESSO VENOSO CENTRAL - HEMODIÁLISE" },
            { 13490, "KIT SONDA VESICAL DE DEMORA" },
            { 13497, "CEFOXITINA 1G FRASCO" },
            { 13577, "NITROFURANTOINA 100MG CP" },
            { 13583, "BENZILPENICILINA 1.200.000 UI" },
            { 14145, "KIT CATETER ACESSO VENOSO CENTRAL MONO LUMEN" },
            { 14171, "KIT BIOPSIA DE PROSTATA" },
            { 14172, "KIT BIOPSIA DE MAMA" },
            { 14178, "KIT RADIO - INTERVENCAO" },
            { 14520, "KIT PUNÇÃO PORT CATH" },
            { 15616, "METRONIDAZOL COMP C/400MG" },
            { 15996, "AMPICILINA 2G + SUBACTAM 1G FR EV" },
            { 16103, "KIT ACIDO ZOLEDRONICO 4MG" },
            { 16104, "KIT PAMIDRONATO DE CALCIO" },
            { 16199, "KIT CIRURGICO DAY CLINIC" },
            { 16704, "KIT - HEMODIÁLISE BBRAUN" },
            { 16730, "KIT AGULHAMENTO" },
            { 9000744, "ACICLOVIR 250MG FRASCO" },
            { 9000796, "AGUA PARA INJECAO SOL INJ AMP C/10ML" },
            { 9002637, "SULFAMETOXAZOL + TRIMETOPRIMA (800+160MG) COMP" },
            { 9004024, "ANFOTERICINA B DESOXICOLATO AMP 50MG PO LIOF INJ" },
            { 9004475, "LIDOCAINA GELEIA 20MG/G USO TOPICO BG C/30G" },
            { 9005384, "CIPROFLOXACINO COMP C/500MG" },
            { 9005650, "SORO FISIOLOGICO 0,9% - 250ML - BOL" },
            { 9009394, "GENTAMICINA 40MG/ML SOL INJ AMP C/80MG - 2ML" },
            { 9010480, "CEFAZOLINA 1G PO INJ CAPAC. 10ML FA C/1G" },
            { 9011071, "METRONIDAZOL 5MG/ML SOL INJ BOLS C/100ML" },
            { 9011684, "MICAFUNGINA 100MG PO LIOF SOL INJ X 1 FA C/100MG" },
            { 9013096, "LEVOFLOXACINO COMP C/500MG" },
            { 9013148, "CEFEPIMA 1G FA EV" },
            { 9013638, "LEVOFLOXACINO 5MG/ML SOL INJ IV BOLS C/500MG" },
            { 9015674, "POLIMIXINA B 500.000 UI PO LIOF P/SOL INJ FA C/500UI/MIL" },
            { 9016630, "TEICOPLANINA 400MG/AMP EV" },
            { 9017228, "AMICACINA 250MG/ML SOL INJ AMP C/ 2ML" },
            { 9017463, "FLUCONAZOL 150MG CP" },
            { 9018620, "TIGECICLINA 50MG PO LIOF INJ FA C/50MG EV" },
            { 9018717, "PIPERACILINA + TAZOBACTAM 4,5 FA EV" },
            { 9019752, "CLINDAMICINA 150MG/ML SOL INJ AMP C/600MG EV" },
            { 9021041, "VANCOMICINA 500MG PO LIOF INJ FA C/500MG" },
            { 9023432, "CIPROFLOXACINO 200MG/100ML BOLSA EV" },
            { 9026283, "NISTATINA 100.000 UI/ML SUSPENSAO ORAL FR C/50ML" },
            { 9026323, "CEFTRIAXONA AMP FA C/1G EV" },
            { 9027155, "OXACILINA SOL INJ IM/IV FA C/500MG" },
            { 9032052, "FLUCONAZOL 2MG/ML SOL INJ INFUS IV BOLS C/100ML" },
            { 9032382, "MEROPENEM 1000MG PO INJ" },
            { 9032984, "LINEZOLIDA 600MG/300ML SOL INJ INFUS IV BOLS" },
        };

        // Carrega semente + cache local (chamar uma vez, no início do app).
        // O cache fica na subpasta "dados" ao lado do exe — criada automaticamente
        // em qualquer máquina (farmácia inclusive), como já acontece com "logs".
        public static void Init(string dirBase)
        {
            lock (_lock)
            {
                _nomes = new Dictionary<int, string>(_semente);
                try
                {
                    string dirDados = Path.Combine(dirBase, "dados");
                    Directory.CreateDirectory(dirDados);
                    _path = Path.Combine(dirDados, "nomes-produtos.txt");

                    // migra o cache de versão anterior (ficava solto ao lado do exe)
                    string antigo = Path.Combine(dirBase, "nomes-produtos.txt");
                    if (File.Exists(antigo) && !File.Exists(_path)) File.Move(antigo, _path);

                    if (File.Exists(_path))
                    {
                        foreach (var ln in File.ReadAllLines(_path, Encoding.UTF8))
                        {
                            int p = ln.IndexOf('|');
                            if (p <= 0) continue;
                            int cd; if (!int.TryParse(ln.Substring(0, p).Trim(), out cd)) continue;
                            string nm = ln.Substring(p + 1).Trim();
                            if (nm.Length > 0) _nomes[cd] = nm;
                        }
                    }
                }
                catch (Exception ex) { Logger.Log("ProdutoNomes: falha ao preparar o cache: " + ex.Message); }
            }
        }

        public static string Get(int codigo)
        {
            lock (_lock) { string n; return _nomes.TryGetValue(codigo, out n) ? n : null; }
        }

        // Incorpora pares novos e persiste no cache local; retorna quantos eram inéditos.
        public static int Aprender(Dictionary<int, string> pares)
        {
            if (pares == null || pares.Count == 0) return 0;
            lock (_lock)
            {
                int novos = 0;
                foreach (var kv in pares)
                {
                    if (kv.Value == null || kv.Value.Length < 3) continue;
                    string atual;
                    if (!_nomes.TryGetValue(kv.Key, out atual) || atual != kv.Value) { _nomes[kv.Key] = kv.Value; novos++; }
                }
                if (novos > 0 && _path != null)
                {
                    try
                    {
                        var sb = new StringBuilder();
                        foreach (var kv in _nomes)
                        {
                            string sem;
                            if (_semente.TryGetValue(kv.Key, out sem) && sem == kv.Value) continue;  // só o que difere da semente
                            sb.AppendLine(kv.Key + "|" + kv.Value);
                        }
                        File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);
                    }
                    catch (Exception ex) { Logger.Log("ProdutoNomes: falha ao gravar o cache: " + ex.Message); }
                }
                return novos;
            }
        }
    }
}
