using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulmvKit.Core
{
    // Um antimicrobiano prescrito e vigente, do jeito que o PEP entrega.
    //
    // O que importa para a farmácia está em três campos que o MV mantém separados:
    //   Quantidade   -> quantas unidades POR HORÁRIO (1, 2, 3 ampolas...)
    //   Frequencia   -> de quantas em quantas horas ("08|08H - 08|08H")
    //   Dias         -> "D5/7" = quinto dia de sete previstos
    // Errar qualquer um deles erra a previsão inteira: VANCOMICINA 2 FA de 12/12h
    // são 4 frascos por dia, não 2.
    public class ItemAntimicrobiano
    {
        public int CdPreMed;          // prescrição a que o item pertence
        public int ItemId;            // id do item prescrito
        public string Produto;        // "VANCOMICINA 500MG PO LIOF INJ FA C/500MG"
        public decimal Quantidade;    // unidades POR HORÁRIO
        public string Unidade;        // "FA C/500MG"
        public string Frequencia;     // "12|12H - 12|12H", "- - 8HRS MANHA"
        public string Via;            // "ENDOVENOSA"
        public string Dias;           // "D5/7" (pode vir vazio: uso contínuo)
        public bool Cronico;

        public int DiaAtual;          // 5 em "D5/7" (0 = não informado)
        public int DiasPrevistos;     // 7 em "D5/7" (0 = não informado)

        // Aplicações por dia deduzidas da frequência. 0 = não reconhecida — a tela
        // TEM que mostrar isso em vez de assumir 1, senão a previsão sai pela metade.
        public int AplicacoesPorDia;
        public bool FrequenciaEstimada;   // veio da tabela de apoio, não do texto
        // "AGORA", "DOSE UNICA", "SE NECESSARIO": consome hoje, mas não se repete —
        // projetar isso para os próximos dias inflaria a previsão.
        public bool NaoRecorrente;

        // Paciente: nem sempre dá para saber (ver PepClient). Vazio = não identificado.
        public int PacienteId;
        public string Paciente;
        public string Setor;
        public int Atendimento;

        // Justificativa de uso (ver CruzamentoJustificativas).
        public bool ExigeJustificativa = true;
        public DateTime JustificadoEm;      // MinValue = nenhuma justificativa cobre este item
        public bool Justificado { get { return JustificadoEm != DateTime.MinValue; } }

        // Quando este antimicrobiano começou, deduzido do "D5/7": no dia 5, começou há 4.
        // É o que permite dizer se a justificativa do paciente é ANTERIOR a este tratamento
        // — o médico pode ter justificado outro antibiótico e depois trocado.
        public DateTime InicioTratamento
        {
            get { return DiaAtual > 0 ? DateTime.Today.AddDays(-(DiaAtual - 1)) : DateTime.MinValue; }
        }

        // Ligação com o estoque (ver PonteEstoque). CodigoProduto = 0 quando não foi
        // possível ligar o nome da prescrição a um produto do catálogo.
        public int CodigoProduto;
        public string ProdutoEstoque;    // nome do produto no estoque (para conferência)
        public decimal Estoque;
        public bool EstoqueConhecido;

        // Quantas EMBALAGENS por horário (ver ConversaoDose). É igual à Quantidade quando
        // a prescrição já veio em embalagem; convertida quando veio em medida (250 MG do
        // frasco de 500 mg = 1 frasco); -1 quando veio em medida sem conversão possível —
        // e aí o item fica fora da soma, pela Ressalva.
        public decimal QuantidadePorAplicacao
        {
            get { return ConversaoDose.PorAplicacao(Quantidade, Unidade, Via, Produto, ProdutoEstoque); }
        }

        // true = a dose prescrita era medida (mg/UI/mL) e virou embalagens na conta.
        // A tela usa para explicar o número: sem isso, "250 MILIGRAMA" na coluna da dose
        // e "1 por horário" no tooltip parecem contradição.
        public bool DoseConvertida
        {
            get
            {
                decimal f; char b;
                return Quantidade > 0 && ConversaoDose.MedidaPura(Unidade, out f, out b)
                    && QuantidadePorAplicacao >= 0;
            }
        }

        public decimal ConsumoPorDia
        {
            get
            {
                decimal q = QuantidadePorAplicacao;
                return q > 0 ? q * AplicacoesPorDia : 0;
            }
        }

        // Dias que ainda faltam CONTANDO HOJE. Em "D5/7" restam 3 — os dias 5, 6 e 7.
        //
        // O dia corrente conta: o paciente recebe as doses dele até o fim do dia, então em
        // "D7/7" ainda falta um dia inteiro de medicamento. Contar só "depois de hoje"
        // subtraía um dia de CADA tratamento da previsão e fazia o último dia aparecer
        // como se não precisasse de mais nada.
        //
        // Se o médico passou do previsto (D8/7), não sobra nada a dispensar.
        public int DiasRestantes
        {
            get
            {
                if (DiasPrevistos <= 0 || DiaAtual <= 0) return -1;   // -1 = sem previsão de término
                int r = DiasPrevistos - DiaAtual + 1;
                return r > 0 ? r : 0;
            }
        }

        // Quanto ainda falta dispensar deste item.
        //
        // REGRA DE PISO: item vigente NUNCA projeta zero. Se o programa sabe a dose e a
        // frequência, ele sabe o consumo de HOJE — e hoje o paciente recebe. Antes, três
        // situações devolviam zero e o total do produto saía menor com cara de completo:
        //
        //   dose avulsa ("AGORA")  -> era 0; agora conta UMA vez, hoje. Ela vai ser
        //                             dispensada; o que não se pode é repeti-la nos
        //                             próximos dias, e isso continua valendo.
        //   sem previsão de término (campo "Dias" vazio, uso contínuo) -> era 0; agora
        //                             conta hoje. É um PISO: o tratamento continua, e o
        //                             banner avisa que o número é só do dia.
        //   passou do previsto ("D8/7") -> era 0; agora conta hoje. A prescrição está
        //                             vigente, então o remédio sai hoje de qualquer jeito.
        //
        // O único zero que resta é o zero honesto: quando não dá para saber a dose por dia
        // (frequência não reconhecida ou quantidade não lida). Esse não se inventa.
        public decimal APrever
        {
            get
            {
                if (AplicacoesPorDia <= 0 || Quantidade <= 0 || QuantidadePorAplicacao < 0) return 0;
                if (NaoRecorrente) return ConsumoPorDia;   // uma vez, hoje
                int d = DiasRestantes;
                return d > 0 ? ConsumoPorDia * d : ConsumoPorDia;
            }
        }

        // Por que este item NÃO entrou na soma (vazio = entrou). Só sobrou o que é
        // genuinamente desconhecido — casos que a farmácia consegue resolver editando
        // dados/frequencias.txt, o cadastro da apresentação ou a prescrição no MV.
        public string Ressalva
        {
            get
            {
                if (AplicacoesPorDia <= 0) return "frequência não reconhecida";
                if (Quantidade <= 0) return "quantidade não informada";
                if (QuantidadePorAplicacao < 0)
                    return "dose em " + Unidade + " sem apresentação conhecida para converter";
                return "";
            }
        }

        // Entrou na soma, mas o número é um PISO: cobre só o dia de hoje porque o MV não
        // diz até quando o tratamento vai. Vazio = a conta cobre o tratamento inteiro.
        //
        // A dose avulsa NÃO entra aqui: "uma vez, hoje" é a conta completa dela, não uma
        // estimativa por baixo. Marcá-la como piso faria a tela pedir desconfiança de um
        // número que está certo.
        public string SoHoje
        {
            get
            {
                if (Ressalva.Length > 0 || NaoRecorrente) return "";
                if (DiasPrevistos <= 0) return "uso contínuo, sem previsão de término";
                if (DiaAtual <= 0) return "o MV não informou o dia do tratamento";
                if (DiaAtual > DiasPrevistos) return "já passou dos dias previstos pelo médico";
                return "";
            }
        }
    }

    // Traduz a frequência do MV em "quantas vezes por dia".
    //
    // O texto vem em dois formatos:
    //   "08|08H - 08|08H"  -> intervalo fixo, dá para ler direto (24/8 = 3x ao dia)
    //   "- - 8HRS MANHA"   -> horário nomeado; o texto não diz quantas vezes.
    // Para o segundo caso existe dados/frequencias.txt, que a farmácia edita.
    // O que não estiver em nenhum dos dois casos volta 0 e a tela marca a linha —
    // é melhor a pessoa ver "não sei" do que somar um número errado.
    public static class Posologia
    {
        private static string _caminho;
        private static readonly Dictionary<string, int> _tabela =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Rótulos já vistos no hospital. O valor é um ponto de partida a conferir:
        // "8HRS MANHA" é dose única da manhã (azitromicina, ceftriaxona, amicacina).
        private static readonly string[] Semente = {
            "8HRS MANHA=1",
            "12HRS TARDE=1",
            "20HRS NOITE=1",
        };

        public static void Init(string baseDir)
        {
            string dir = Path.Combine(baseDir, "dados");
            Directory.CreateDirectory(dir);
            _caminho = Path.Combine(dir, "frequencias.txt");
            _tabela.Clear();
            foreach (string s in Semente) Ler(s);

            try
            {
                if (!File.Exists(_caminho)) { CriarModelo(); return; }
                foreach (var linha in File.ReadAllLines(_caminho)) Ler(linha);
                Logger.Log("Posologia: " + _tabela.Count + " frequências nomeadas (arquivo " + _caminho + ").");
            }
            catch (Exception ex) { Logger.Log("Posologia: falha ao ler " + _caminho + ": " + ex.Message); }
        }

        private static void Ler(string linha)
        {
            string s = (linha ?? "").Trim();
            if (s.Length == 0 || s.StartsWith("#")) return;
            int cerq = s.IndexOf('#');
            if (cerq >= 0) s = s.Substring(0, cerq).Trim();
            int ig = s.IndexOf('=');
            if (ig <= 0) return;
            string nome = Normalizar(s.Substring(0, ig));
            int vezes;
            if (!int.TryParse(s.Substring(ig + 1).Trim(), out vezes) || vezes < 0) return;
            _tabela[nome] = vezes;
        }

        private static void CriarModelo()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Quantas vezes POR DIA vale cada frequencia com horario nomeado.");
                sb.AppendLine("#");
                sb.AppendLine("# As frequencias com intervalo (\"08|08H\", \"12|12H\") o programa le sozinho.");
                sb.AppendLine("# Este arquivo e so para as nomeadas, em que o texto nao diz o numero de vezes.");
                sb.AppendLine("# O que nao estiver aqui aparece na tela marcado como \"frequencia nao reconhecida\"");
                sb.AppendLine("# e NAO entra na soma da previsao -- de proposito, para nao inventar numero.");
                sb.AppendLine("#");
                sb.AppendLine("# Formato:  NOME DA FREQUENCIA=vezes por dia");
                sb.AppendLine("# Depois de editar, feche e abra o programa.");
                sb.AppendLine();
                sb.AppendLine("# CONFIRA estes valores com a farmacia -- vieram de um palpite inicial:");
                foreach (string s in Semente) sb.AppendLine(s);
                File.WriteAllText(_caminho, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Logger.Log("Posologia: não consegui criar " + _caminho + ": " + ex.Message); }
        }

        // Frequências que consomem HOJE mas não se repetem: projetá-las para os dias
        // seguintes inflaria a previsão. "AGORA" costuma vir junto de um esquema regular
        // do mesmo antibiótico (a dose de ataque), então some duas vezes se não separar.
        private static readonly string[] Avulsas = { "AGORA", "DOSE UNICA", "SE NECESSARIO", "A CRITERIO", "ACM", "S/N" };

        public static bool EhAvulsa(string frequencia)
        {
            string f = Normalizar(frequencia);
            foreach (string a in Avulsas) if (f.IndexOf(a, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Devolve as aplicações por dia; estimada=true quando veio da tabela de apoio.
        public static int AplicacoesPorDia(string frequencia, out bool estimada)
        {
            estimada = false;
            if (string.IsNullOrEmpty(frequencia)) return 0;

            // 1) Intervalo explícito: "08|08H", "12|12H", "24HS/24HS", "6HS/6HS", "12/12 ENF".
            // Sem o "H" no fim (o caso "12/12 ENF") só aceitamos quando os dois números são
            // iguais — é assim que o MV escreve intervalo, e evita casar com "10-22H".
            var m = Regex.Match(frequencia, @"(\d{1,2})\s*(?:HRS|HS|H)?\s*[|/]\s*(\d{1,2})\s*(HRS|HS|H)?",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int h, h2;
                bool temH = m.Groups[3].Success;
                if (int.TryParse(m.Groups[1].Value, out h) && int.TryParse(m.Groups[2].Value, out h2)
                    && h > 0 && h <= 24 && (temH || h == h2))
                    return (24 + h - 1) / h;   // 8h -> 3; 12h -> 2; 24h -> 1; 7h -> 4 (arredonda pra cima)
            }

            // 2) Avulsa: uma aplicação, hoje. Quem soma é o consumo do dia, não a previsão.
            if (EhAvulsa(frequencia)) return 1;

            // 3) "1X AO DIA", "2 X DIA", "3 VEZES AO DIA".
            m = Regex.Match(frequencia, @"(\d{1,2})\s*(?:X|VEZ(?:ES)?)\s*(?:AO|POR|/)?\s*DIA", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int v;
                if (int.TryParse(m.Groups[1].Value, out v) && v > 0) return v;
            }

            // 4) Horário nomeado: cai na tabela. "- - 8HRS MANHA" -> "8HRS MANHA".
            string nome = Normalizar(Regex.Replace(frequencia, @"^[\s\-]+", ""));
            int vezes;
            if (nome.Length > 0 && _tabela.TryGetValue(nome, out vezes)) { estimada = true; return vezes; }

            // 5) Um único horário do dia: "8HRS MANHA", "16HRS TARDE", "20H NOITE".
            // É um horário só, logo uma aplicação por dia. Fica marcado como estimada para
            // a farmácia poder conferir, e dá para sobrescrever no frequencias.txt.
            if (Regex.IsMatch(nome, @"^\d{1,2}\s*(?:HRS|HS|H)?\s+(MANHA|TARDE|NOITE|MADRUGADA)$"))
            { estimada = true; return 1; }

            return 0;   // não reconhecida — a tela marca e não soma
        }

        // "D5/7" -> dia 5 de 7. Devolve false quando o campo não veio.
        public static bool LerDias(string administrationDays, out int atual, out int total)
        {
            atual = 0; total = 0;
            if (string.IsNullOrEmpty(administrationDays)) return false;
            var m = Regex.Match(administrationDays, @"D\s*(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Value, out atual) & int.TryParse(m.Groups[2].Value, out total);
        }

        // Sem acentos e sem espaço dobrado, para o arquivo casar com o que vem do MV.
        private static string Normalizar(string s)
        {
            if (s == null) return "";
            string d = s.Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim().ToUpperInvariant();
        }
    }
}
