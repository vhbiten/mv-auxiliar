using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // Um atendimento de urgência do PAM, lido do form CON_ATE (MorphisClient.ListarAtendimentosUrgenciaAsync).
    public class AtendimentoPam
    {
        public string Atendimento;      // CD_ATENDIMENTO
        public string Paciente;         // DSP_NM_PACIENTE
        public string Idade;            // DSP_IDADE resumida ("79a")
        public string Medico;           // DSP_NM_PRESTADOR
        public string Destino;          // DSP_DS_DES_ATE (ex.: SALA DE EMERGENCIA)
        public DateTime Entrada;        // HR_ATENDIMENTO
        public DateTime? Alta;          // HR_ALTA
        public DateTime? AltaMedica;    // HR_ALTA_MEDICA
        public string MotivoAlta;       // DSP_DS_MOT_ALT (ex.: ALTA APOS MEDICACAO)

        public bool EmAtendimento { get { return !Alta.HasValue && !AltaMedica.HasValue; } }
        public DateTime? Saida { get { return AltaMedica.HasValue ? AltaMedica : Alta; } }
    }

    // Uma prescrição do atendimento no PEP (/prescriptions?hospitalAdmission=). O id é interno
    // do PEP (não é o CD_PRE_MED da solicitação).
    public class PrescricaoPep
    {
        public long Id;
        public string Status;           // OPENED (sendo escrita) / SIGNED / CLOSED
        public DateTime? Criada;
        public DateTime? Fechada;
        public string Modelo;           // PRESCRICAO MEDICA, RECEITUARIO EXTERNO, PRESCRIÇÃO DE ENFERMAGEM...
        public long CdPreMed;           // nº da prescrição no MV (lido da folha; 0 = desconhecido)
        public List<ItemPrescrito> Itens;   // o que foi prescrito (null = não lido)

        // Só a prescrição médica gera pedido de medicamento à farmácia; receituário externo e
        // prescrição de enfermagem não contam para "em elaboração".
        public bool Medica
        {
            get { string m = PainelPam.SemAcento(Modelo); return m.Contains("PRESCRICAO") && m.Contains("MEDICA"); }
        }
        public bool Aberta { get { return string.Equals(Status, "OPENED", StringComparison.OrdinalIgnoreCase); } }
        // Só SIGNED/CLOSED rendem folha e pedido; outro status (cancelada etc.) não conta como assinada.
        public bool Assinada
        {
            get
            {
                return string.Equals(Status, "SIGNED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Status, "CLOSED", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Hora de referência: a da assinatura (fechamento); sem ela, a da criação.
        public DateTime Hora
        {
            get { return Fechada.HasValue ? Fechada.Value : (Criada.HasValue ? Criada.Value : DateTime.MinValue); }
        }
    }

    // Um item da prescrição (/prescriptions/{cdPreMed}/items do PEP + a folha impressa).
    public class ItemPrescrito
    {
        public string Nome;
        public string Esquema;          // tipo no PEP: MDN, MAV, PLN... EXL (exame laboratorial), PEN (cuidado)...
        public bool Medicamento;        // vai à farmácia: medicineType do esquema (DEP, a nutrição parenteral, também)
        public bool Suspenso;           // a prescrição só SUSPENDE o item ("Suspenso dia" na folha)
    }

    // O que se lê uma vez de uma prescrição assinada (PepClient.LerFolhaPrescricaoAsync).
    public class FolhaPrescricao
    {
        public long CdPreMed;
        public List<ItemPrescrito> Itens;
    }

    // O que sobra para a farmácia numa prescrição assinada que não gerou pedido.
    public enum FarmaciaNaPrescricao
    {
        Desconhecido,       // itens não lidos
        Nada,               // só exames, cuidados, dieta, suspensões...
        AguardandoPedido,   // tem medicamento e acabou de ser assinada (o pedido nasce no mesmo minuto)
        SemPedido           // tem medicamento e o pedido não apareceu
    }

    public enum EstadoPrescricaoPam { SemPrescricao, SemPedido, EmElaboracao, Pendente, Atendida }

    // Seção da aba Urgência (PAM), na ordem em que aparece: o que pede a farmácia primeiro.
    public enum GrupoPam { Pendente, Aguardando, Atendidos, Alta }

    // Um item do acordeão do paciente: a prescrição médica E o pedido que ela gerou na farmácia
    // (os dois na mesma linha), ou só um pedido (material, transferência), ou só uma prescrição
    // que não gerou pedido à Farmácia Central.
    public class ItemPam
    {
        public LinhaPam Pai;
        public Solicitacao Pedido;          // null = prescrição sem pedido
        public PrescricaoPep Prescricao;    // null = pedido sem prescrição médica (material etc.)
        public DateTime Quando;
    }

    // Uma linha do painel: o atendimento, suas prescrições no PEP e seus pedidos à Farmácia Central.
    public class LinhaPam
    {
        public AtendimentoPam At;
        public List<PrescricaoPep> Prescricoes = new List<PrescricaoPep>();
        public List<Solicitacao> Pedidos = new List<Solicitacao>();
        public bool PepLido;            // false = o PEP não respondeu para este atendimento
        public bool PepHerdado;         // PEP sem resposta: as prescrições são as da leitura anterior

        public int PedidosPendentes
        {
            get { int n = 0; foreach (var p in Pedidos) if (p.Situacao != "S") n++; return n; }
        }

        // Hora do pedido mais recente (DT_SOLSAI_PRO + HR_SOLSAI_PRO).
        public DateTime? UltimoPedido
        {
            get
            {
                DateTime? max = null;
                foreach (var p in Pedidos)
                {
                    DateTime d;
                    if (PainelPam.DataHoraPedido(p, out d) && (!max.HasValue || d > max.Value)) max = d;
                }
                return max;
            }
        }

        // Médico com prescrição médica aberta (status OPENED) e recente. Uma aberta há mais de
        // MinutosRascunhoParado é rascunho esquecido e não conta.
        public bool MedicoPrescrevendo
        {
            get
            {
                if (!At.EmAtendimento) return false;
                foreach (var p in Prescricoes)
                    if (p.Medica && p.Aberta && p.Criada.HasValue
                        && (DateTime.Now - p.Criada.Value).TotalMinutes <= PainelPam.MinutosRascunhoParado)
                        return true;
                return false;
            }
        }

        public bool TemPrescricaoMedicaAssinada
        {
            get { foreach (var p in Prescricoes) if (p.Medica && p.Assinada) return true; return false; }
        }

        // Já tem prescrição médica: assinada no PEP ou, com o PEP sem resposta, um pedido que veio
        // de prescrição (traz o nº dela). Só pedido de material não conta.
        public bool TemPrescricao
        {
            get
            {
                if (TemPrescricaoMedicaAssinada) return true;
                foreach (var p in Pedidos) if ((p.CdPreMed ?? "").Trim().Length > 0) return true;
                return false;
            }
        }

        // Prescrições médicas assinadas (as que rendem folha no PEP), da mais antiga para a mais nova.
        public List<PrescricaoPep> PrescricoesMedicasAssinadas
        {
            get
            {
                var r = new List<PrescricaoPep>();
                foreach (var p in Prescricoes) if (p.Medica && p.Assinada) r.Add(p);
                r.Sort(delegate(PrescricaoPep a, PrescricaoPep b) { return a.Hora.CompareTo(b.Hora); });
                return r;
            }
        }

        // Pedido pendente (P ou A) vem primeiro: é o que falta entregar. Depois o médico
        // escrevendo, os pedidos já atendidos, prescrição sem pedido à farmácia (exames,
        // cuidados) e, por fim, nenhuma prescrição.
        public EstadoPrescricaoPam Estado
        {
            get
            {
                if (PedidosPendentes > 0) return EstadoPrescricaoPam.Pendente;
                if (MedicoPrescrevendo) return EstadoPrescricaoPam.EmElaboracao;
                if (Pedidos.Count > 0) return EstadoPrescricaoPam.Atendida;
                if (TemPrescricaoMedicaAssinada) return EstadoPrescricaoPam.SemPedido;
                return EstadoPrescricaoPam.SemPrescricao;
            }
        }
    }

    public class PainelPamResult
    {
        public List<LinhaPam> Linhas = new List<LinhaPam>();
        public DateTime Quando;
        public DateTime PedidosQuando;  // hora em que os pedidos de HOJE foram lidos do MV
        public string AvisoPep;         // != null: o PEP não respondeu (sem a informação "em elaboração")
    }

    // Painel do PAM (aba "Urgência (PAM)"): quem está em atendimento no Pronto Atendimento e em que
    // pé está a prescrição de cada um — sem prescrição, médico prescrevendo, prescrição pendente
    // na farmácia ou prescrição atendida — e quem já teve alta. SÓ LEITURA, em três fontes
    // validadas ao vivo em 24/10/2026:
    //   1. form CON_ATE (Urgência e Emergência > Consultas > Atendimento): a lista OFICIAL dos
    //      atendimentos do dia, inclusive quem ainda não foi visto pelo médico, com alta e motivo;
    //   2. form M_BAIXASOL (o mesmo da aba Solicitações): os pedidos à Farmácia Central de cada
    //      atendimento, com a situação (P/A/S) — a solicitação nasce no minuto em que o médico
    //      assina a prescrição;
    //   3. PEP: as prescrições do atendimento; a que está OPENED é o médico escrevendo agora.
    // As duas primeiras falam com o servidor de forms (precisam do MvGate); a do PEP não.
    public static class PainelPam
    {
        public const int EstoqueFarmacia = 5;
        public const int MinutosAltaVisivel = 60;       // quem teve alta continua na lista por 1 h, em cinza
        public const int MinutosRascunhoParado = 60;    // prescrição aberta há mais que isso não é "escrevendo"
        public const int MinutosGerarPedido = 3;        // assinada há menos que isso: o pedido ainda pode estar a caminho
        private const int ParaleloPep = 6;
        public const int DiasAnteriores = 2;                // quem entrou anteontem à noite e ainda espera leito no PAM
        public const int MinutosCacheDiasAnteriores = 5;    // pedidos de dias anteriores mudam pouco: relidos a cada 5 min
        // Atendimentos de dias anteriores: o CON_ATE de um dia leva < 1 s (log de 26/10/2026), e é
        // dali que sai a ALTA de quem virou a noite no PAM — 5 min de atraso era demais.
        public const int SegundosCacheAtendimentosAnteriores = 60;
        // Pedidos de hoje sem a foto das Solicitações (aba delas na CAF ou com filtro): a varredura
        // própria leva ~8 s no portão do MV (6 páginas num dia de 246 pedidos) — no máximo 1 por minuto.
        public const int SegundosCachePedidosHoje = 60;

        // Pedidos de dias anteriores só mudam quando um pedido antigo é atendido. Guardados por
        // MinutosCacheDiasAnteriores, a atualização curta não refaz consultas pesadas no servidor
        // de forms (cada varredura de pedidos de um dia leva ~8 s e prende o MvGate). Tem nome de
        // paciente: LimparCache no logout/expiração.
        private class Lido<T> { public DateTime Quando; public T Valor; }
        private static readonly Dictionary<DateTime, Lido<List<AtendimentoPam>>> _atendimentosDia = new Dictionary<DateTime, Lido<List<AtendimentoPam>>>();
        private static readonly Dictionary<DateTime, Lido<List<Solicitacao>>> _pedidosDia = new Dictionary<DateTime, Lido<List<Solicitacao>>>();

        public static void LimparCache()
        {
            lock (_atendimentosDia) _atendimentosDia.Clear();
            lock (_pedidosDia) _pedidosDia.Clear();
        }

        // O guardado há menos de 'segundos' (null = não há ou venceu).
        private static Lido<T> DoCache<T>(Dictionary<DateTime, Lido<T>> cache, DateTime dia, int segundos)
        {
            lock (cache)
            {
                Lido<T> l;
                if (cache.TryGetValue(dia, out l) && (DateTime.Now - l.Quando).TotalSeconds < segundos) return l;
                return null;
            }
        }

        private static Lido<T> Guardar<T>(Dictionary<DateTime, Lido<T>> cache, DateTime dia, T valor)
        {
            lock (cache)
            {
                var l = new Lido<T>(); l.Quando = DateTime.Now; l.Valor = valor;
                cache[dia] = l;
                // só os dias que ainda interessam
                var velhos = new List<DateTime>();
                foreach (var d in cache.Keys) if (d < DateTime.Today.AddDays(-DiasAnteriores)) velhos.Add(d);
                foreach (var d in velhos) cache.Remove(d);
                return l;
            }
        }

        // Hoje é sempre lido (é de onde vêm os pacientes novos e as altas do dia).
        private static async Task<List<AtendimentoPam>> AtendimentosDoDiaAsync(Session s, DateTime dia)
        {
            bool passado = dia < DateTime.Today;
            if (passado) { var c = DoCache(_atendimentosDia, dia, SegundosCacheAtendimentosAnteriores); if (c != null) return c.Valor; }
            var lista = await MorphisClient.ListarAtendimentosUrgenciaAsync(s, dia);
            if (passado) Guardar(_atendimentosDia, dia, lista);
            return lista;
        }

        // A lista e a hora em que ela foi lida do MV (a do cache, se veio dele).
        private static async Task<Lido<List<Solicitacao>>> PedidosDoDiaAsync(Session s, DateTime dia)
        {
            int validade = dia < DateTime.Today ? MinutosCacheDiasAnteriores * 60 : SegundosCachePedidosHoje;
            var c = DoCache(_pedidosDia, dia, validade);
            if (c != null) return c;
            var lista = (await MorphisClient.ListarDiaAsync(s, EstoqueFarmacia, "", "", dia)).Itens;
            return Guardar(_pedidosDia, dia, lista);
        }

        // Parte do servidor de forms (chamar DENTRO do MvGate): atendimentos de hoje e dos
        // DiasAnteriores (quem virou a noite no PAM ainda está ativo) e os pedidos à farmácia de
        // cada um, desde o dia da entrada mais antiga da lista.
        public static Task<PainelPamResult> LerMvAsync(Session s)
        {
            return LerMvAsync(s, null, DateTime.MinValue);
        }

        // pedidosDeHoje: a foto recente que o monitor da aba Solicitações já tirou dos pedidos
        // de hoje do estoque da farmácia, tirada às quandoFoto (null = busca aqui). Poupa a
        // varredura mais pesada do ciclo — sobra o CON_ATE.
        public static async Task<PainelPamResult> LerMvAsync(Session s, List<Solicitacao> pedidosDeHoje, DateTime quandoFoto)
        {
            var res = new PainelPamResult();
            DateTime hoje = DateTime.Today;

            var porAt = new Dictionary<string, LinhaPam>();
            for (int k = DiasAnteriores; k >= 0; k--)
            {
                var lista = await AtendimentosDoDiaAsync(s, hoje.AddDays(-k));
                foreach (var a in lista)
                {
                    if (!a.EmAtendimento && a.Saida.HasValue
                        && (DateTime.Now - a.Saida.Value).TotalMinutes > MinutosAltaVisivel) continue;
                    var l = new LinhaPam(); l.At = a;
                    porAt[a.Atendimento] = l;
                }
            }

            DateTime desde = hoje;
            foreach (var l in porAt.Values) if (l.At.Entrada.Date < desde) desde = l.At.Entrada.Date;
            for (DateTime d = desde; d < hoje; d = d.AddDays(1))
                Distribuir(porAt, (await PedidosDoDiaAsync(s, d)).Valor);
            if (pedidosDeHoje != null)
            {
                Distribuir(porAt, pedidosDeHoje);
                res.PedidosQuando = quandoFoto;
            }
            else
            {
                var lidos = await PedidosDoDiaAsync(s, hoje);
                Distribuir(porAt, lidos.Valor);
                res.PedidosQuando = lidos.Quando;
            }

            res.Linhas = new List<LinhaPam>(porAt.Values);
            Ordenar(res.Linhas);
            res.Quando = DateTime.Now;
            return res;
        }

        // Põe cada pedido na linha do atendimento dele (devolução não é entrega).
        private static void Distribuir(Dictionary<string, LinhaPam> porAt, IEnumerable<Solicitacao> pedidos)
        {
            foreach (var sol in pedidos)
            {
                if (sol.OrigemEfetiva == "DEV") continue;
                LinhaPam l;
                if (porAt.TryGetValue((sol.Atendimento ?? "").Trim(), out l)) l.Pedidos.Add(sol);
            }
        }

        // Foto nova dos pedidos de HOJE (monitor das Solicitações, a cada ~30 s): troca a situação
        // dos pedidos de cada paciente na hora, sem consultar o MV e sem esperar o ciclo do PAM.
        // Os pedidos de dias anteriores ficam como estavam. Troca a LISTA (não mexe na antiga):
        // a grade continua lendo a anterior até redesenhar.
        public static void TrocarPedidosDeHoje(List<LinhaPam> linhas, List<Solicitacao> pedidosDeHoje)
        {
            DateTime hoje = DateTime.Today;
            var porAt = new Dictionary<string, LinhaPam>();
            foreach (var l in linhas)
            {
                var antigos = new List<Solicitacao>();
                foreach (var p in l.Pedidos)
                {
                    DateTime d;
                    if (DataHoraPedido(p, out d) && d.Date < hoje) antigos.Add(p);
                }
                l.Pedidos = antigos;
                porAt[l.At.Atendimento] = l;
            }
            Distribuir(porAt, pedidosDeHoje);
        }

        // Parte do PEP (fora do MvGate): prescrições de cada paciente da lista (também de quem
        // teve alta há pouco, para o acordeão dele ainda abrir a folha) e o nº do MV de cada
        // prescrição médica assinada, que é o que a casa com o pedido na farmácia.
        public static async Task CompletarPepAsync(Session s, PainelPamResult res)
        {
            var ativos = new List<LinhaPam>(res.Linhas);
            if (ativos.Count == 0) return;
            if (!await PepClient.EntrarAsync(s))
            {
                res.AvisoPep = "O PEP não respondeu: a informação \"médico prescrevendo\" fica sem atualizar.";
                return;
            }
            var falharam = await LerPrescricoesAsync(s, ativos);
            if (falharam.Count > 0)
            {
                // Sessão do PEP vencida no meio (já aconteceu em leituras longas à tarde): o
                // EntrarAsync valida e, se preciso, reabre — UMA vez — e repete só quem falhou.
                if (await PepClient.EntrarAsync(s)) falharam = await LerPrescricoesAsync(s, falharam);
            }
            int falhas = falharam.Count;
            if (falhas > 0)
                res.AvisoPep = "O PEP não respondeu para " + falhas + " paciente(s): para eles, \"médico prescrevendo\" fica sem dado.";

            // Nº do MV e itens de cada prescrição médica assinada (cache: só as novas vão ao PEP).
            var assinadas = new List<PrescricaoPep>();
            foreach (var l in ativos)
                foreach (var p in l.PrescricoesMedicasAssinadas) assinadas.Add(p);
            for (int i = 0; i < assinadas.Count; i += ParaleloPep)
            {
                var lote = new List<Task<FolhaPrescricao>>();
                var donos = new List<PrescricaoPep>();
                for (int k = i; k < assinadas.Count && k < i + ParaleloPep; k++)
                {
                    donos.Add(assinadas[k]);
                    lote.Add(PepClient.LerFolhaPrescricaoAsync(s, assinadas[k].Id));
                }
                var folhas = await Task.WhenAll(lote);
                for (int k = 0; k < donos.Count; k++)
                {
                    if (folhas[k] == null) continue;
                    donos[k].CdPreMed = folhas[k].CdPreMed;
                    donos[k].Itens = folhas[k].Itens;
                }
            }
        }

        // PEP fora numa atualização: sem isto, quem não tem pedido virava "Sem prescrição" e os
        // ícones da prescrição sumiam. Quem o PEP não respondeu fica com as prescrições da leitura
        // anterior (marcado PepHerdado). Devolve quantas linhas ficaram sem dado nenhum.
        public static int HerdarPrescricoes(PainelPamResult novo, List<LinhaPam> anteriores)
        {
            var antes = new Dictionary<string, LinhaPam>();
            if (anteriores != null) foreach (var l in anteriores) antes[l.At.Atendimento] = l;
            int sem = 0;
            foreach (var l in novo.Linhas)
            {
                if (l.PepLido) continue;
                LinhaPam a;
                if (antes.TryGetValue(l.At.Atendimento, out a) && (a.PepLido || a.PepHerdado))
                {
                    l.Prescricoes = a.Prescricoes;
                    l.PepHerdado = true;
                }
                else sem++;
            }
            return sem;
        }

        // ===== O QUE TEM NA PRESCRIÇÃO QUE NÃO GEROU PEDIDO =====
        // Prescrição médica assinada sem pedido à farmácia quase sempre é só exame, cuidado ou
        // SUSPENSÃO de item (o médico suspende um medicamento e o PEP assina uma prescrição só
        // com isso) — visto ao vivo em 24/10/2026. Medicamento ativo sem pedido é a exceção.
        public static FarmaciaNaPrescricao Farmacia(PrescricaoPep p)
        {
            if (p.Itens == null) return FarmaciaNaPrescricao.Desconhecido;
            foreach (var it in p.Itens)
                if (it.Medicamento && !it.Suspenso)
                    return (DateTime.Now - p.Hora).TotalMinutes < MinutosGerarPedido
                        ? FarmaciaNaPrescricao.AguardandoPedido : FarmaciaNaPrescricao.SemPedido;
            return FarmaciaNaPrescricao.Nada;
        }

        // O pior caso entre as prescrições do paciente que NÃO geraram pedido (as que ficam
        // sozinhas no acordeão; itens = PainelPam.Itens). Sem nenhum pedido, são todas.
        public static FarmaciaNaPrescricao FarmaciaSemPedido(List<ItemPam> itens)
        {
            var r = FarmaciaNaPrescricao.Nada;
            foreach (var it in itens)
            {
                if (it.Pedido != null || it.Prescricao == null) continue;
                var f = Farmacia(it.Prescricao);
                if (f == FarmaciaNaPrescricao.SemPedido) return f;
                if (f == FarmaciaNaPrescricao.AguardandoPedido) r = f;
                else if (f == FarmaciaNaPrescricao.Desconhecido && r == FarmaciaNaPrescricao.Nada) r = f;
            }
            return r;
        }

        // Seção do paciente na aba (regras do usuário, 26/10/2026):
        //  - Alta: teve alta. Pedido que ficou pendente aparece no acordeão dele;
        //  - Prescrição pendente: pedido à farmácia não atendido, ou prescrição assinada com
        //    medicamento que ainda não virou pedido. Quem estava em Atendidos (só exames, por
        //    exemplo) volta para cá quando ganha prescrição de medicamento;
        //  - Aguardando prescrição: nenhuma prescrição médica ainda, ou médico prescrevendo;
        //  - Atendidos: todos os pedidos atendidos, ou prescrição só com exames/cuidados.
        public static GrupoPam Grupo(LinhaPam l, List<ItemPam> itens)
        {
            if (!l.At.EmAtendimento) return GrupoPam.Alta;
            if (l.PedidosPendentes > 0) return GrupoPam.Pendente;
            var f = FarmaciaSemPedido(itens);
            if (f == FarmaciaNaPrescricao.SemPedido || f == FarmaciaNaPrescricao.AguardandoPedido) return GrupoPam.Pendente;
            if (l.MedicoPrescrevendo || !l.TemPrescricao) return GrupoPam.Aguardando;
            return GrupoPam.Atendidos;
        }

        private static readonly string[] OrdemCategorias = {
            "exames laboratoriais", "exames de imagem", "exames", "cuidados", "dieta",
            "fisioterapia", "oxigênio", "hemocomponente", "outros itens" };

        private static string Categoria(string esquema)
        {
            string e = (esquema ?? "").ToUpperInvariant();
            if (e == "EXL") return "exames laboratoriais";
            if (e == "EXI") return "exames de imagem";
            if (e.StartsWith("EX")) return "exames";
            if (e == "PEN" || e == "PME") return "cuidados";
            if (e == "DIE" || e == "DET") return "dieta";
            if (e == "PFI") return "fisioterapia";
            if (e == "GAS") return "oxigênio";
            if (e == "HEM") return "hemocomponente";
            return "outros itens";
        }

        // Em poucas palavras: "Exames laboratoriais", "Suspensão de metoprolol", "Cuidados",
        // "Exames laboratoriais + cuidados", "Medicamento + dieta". null = itens não lidos.
        public static string Conteudo(PrescricaoPep p)
        {
            if (p.Itens == null) return null;
            if (p.Itens.Count == 0) return "Prescrição sem itens";
            var ativos = new List<ItemPrescrito>();
            var suspensos = new List<ItemPrescrito>();
            foreach (var it in p.Itens) (it.Suspenso ? suspensos : ativos).Add(it);

            var partes = new List<string>();
            int med = 0;
            foreach (var it in ativos) if (it.Medicamento) med++;
            if (med > 0) partes.Add(med == 1 ? "medicamento" : med + " medicamentos");
            if (suspensos.Count == 1) partes.Add("suspensão de " + NomeCurto(suspensos[0].Nome));
            else if (suspensos.Count > 1) partes.Add("suspensão de " + Juntos(suspensos));
            foreach (string cat in OrdemCategorias)
                foreach (var it in ativos)
                    if (!it.Medicamento && Categoria(it.Esquema) == cat) { partes.Add(cat); break; }

            string r = string.Join(" + ", partes.ToArray());
            return char.ToUpperInvariant(r[0]) + r.Substring(1);
        }

        // Vários suspensos de uma vez: "3 medicamentos", "exames laboratoriais" (todos do mesmo
        // tipo) ou "4 itens".
        private static string Juntos(List<ItemPrescrito> itens)
        {
            bool todosMed = true;
            string cat = null;
            foreach (var it in itens)
            {
                if (!it.Medicamento) todosMed = false;
                string c = it.Medicamento ? "" : Categoria(it.Esquema);
                if (cat == null) cat = c; else if (cat != c) cat = "";
            }
            if (todosMed) return itens.Count + " medicamentos";
            if (!string.IsNullOrEmpty(cat)) return cat;
            return itens.Count + " itens";
        }

        // "METOPROLOL 1MG/1ML  AMP 5ML" -> "metoprolol"; "SORO FISIOLOGICO 0,9% 500ML" -> "soro fisiologico".
        public static string NomeCurto(string nome)
        {
            var palavras = new List<string>();
            foreach (string w in (nome ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                bool temDigito = false;
                foreach (char c in w) if (char.IsDigit(c)) { temDigito = true; break; }
                if (temDigito || w.StartsWith("(") || w.Contains("/") || w == "-" || w == "+") break;
                palavras.Add(w);
                if (palavras.Count == 3) break;
            }
            string r = palavras.Count > 0 ? string.Join(" ", palavras.ToArray()) : (nome ?? "").Trim();
            return r.ToLowerInvariant();
        }

        // Itens do acordeão, em ordem de hora. Cada pedido vira uma linha; o pedido que a
        // prescrição gerou leva a prescrição junto (casados pelo nº da prescrição no MV, o
        // CD_PRE_MED do pedido). Prescrição que não gerou pedido à Farmácia Central fica numa
        // linha própria. Sem o nº (folha não lida), casa pela hora: a solicitação nasce no
        // minuto da assinatura (medido em 24/10/2026), então aceita de 1 min antes a 3 depois.
        public static List<ItemPam> Itens(LinhaPam l)
        {
            var res = new List<ItemPam>();
            var assinadas = l.PrescricoesMedicasAssinadas;
            var usadas = new HashSet<PrescricaoPep>();

            foreach (var ped in l.Pedidos)
            {
                var it = new ItemPam(); it.Pai = l; it.Pedido = ped;
                DateTime h; it.Quando = DataHoraPedido(ped, out h) ? h : DateTime.MinValue;
                string cd = (ped.CdPreMed ?? "").Trim();
                if (cd.Length > 0)
                    foreach (var p in assinadas)
                        if (p.CdPreMed > 0 && p.CdPreMed.ToString() == cd) { it.Prescricao = p; break; }
                res.Add(it);
                if (it.Prescricao != null) usadas.Add(it.Prescricao);
            }
            // Casamento pela hora, só para pedido de prescrição sem par e prescrição sem nº: a
            // prescrição mais próxima da hora do pedido (duas assinadas em poucos minutos não se
            // cruzam).
            foreach (var it in res)
            {
                if (it.Prescricao != null || (it.Pedido.CdPreMed ?? "").Trim().Length == 0 || it.Quando == DateTime.MinValue) continue;
                PrescricaoPep melhor = null;
                double menor = double.MaxValue;
                foreach (var p in assinadas)
                {
                    if (usadas.Contains(p) || p.CdPreMed > 0) continue;
                    double dif = (it.Quando - p.Hora).TotalMinutes;
                    if (dif >= -1 && dif <= 3 && Math.Abs(dif) < menor) { menor = Math.Abs(dif); melhor = p; }
                }
                if (melhor != null) { it.Prescricao = melhor; usadas.Add(melhor); }
            }
            foreach (var p in assinadas)
            {
                if (usadas.Contains(p)) continue;
                var it = new ItemPam(); it.Pai = l; it.Prescricao = p; it.Quando = p.Hora;
                res.Add(it);
            }
            res.Sort(delegate(ItemPam a, ItemPam b) { return a.Quando.CompareTo(b.Quando); });
            return res;
        }

        // Lê as prescrições em lotes paralelos; devolve as linhas em que o PEP não respondeu.
        private static async Task<List<LinhaPam>> LerPrescricoesAsync(Session s, List<LinhaPam> alvos)
        {
            var falharam = new List<LinhaPam>();
            for (int i = 0; i < alvos.Count; i += ParaleloPep)
            {
                var lote = new List<Task<List<PrescricaoPep>>>();
                var donos = new List<LinhaPam>();
                for (int k = i; k < alvos.Count && k < i + ParaleloPep; k++)
                {
                    donos.Add(alvos[k]);
                    lote.Add(PepClient.PrescricoesDoAtendimentoAsync(s, alvos[k].At.Atendimento));
                }
                var resultados = await Task.WhenAll(lote);
                for (int k = 0; k < donos.Count; k++)
                {
                    if (resultados[k] == null) { falharam.Add(donos[k]); continue; }
                    donos[k].Prescricoes = resultados[k];
                    donos[k].PepLido = true;
                }
            }
            return falharam;
        }

        // Ordem estável (as linhas não pulam a cada atualização): quem está em atendimento, por
        // hora de entrada (há mais tempo no PAM primeiro); depois as altas, a mais recente primeiro.
        // A aba separa por seção (Grupo) e mantém esta ordem dentro de cada uma.
        public static void Ordenar(List<LinhaPam> linhas)
        {
            linhas.Sort(delegate(LinhaPam a, LinhaPam b)
            {
                bool ea = a.At.EmAtendimento, eb = b.At.EmAtendimento;
                if (ea != eb) return ea ? -1 : 1;
                if (ea) return a.At.Entrada.CompareTo(b.At.Entrada);
                DateTime sa = a.At.Saida.HasValue ? a.At.Saida.Value : DateTime.MinValue;
                DateTime sb = b.At.Saida.HasValue ? b.At.Saida.Value : DateTime.MinValue;
                return sb.CompareTo(sa);
            });
        }

        public static string Rotulo(EstadoPrescricaoPam e)
        {
            switch (e)
            {
                case EstadoPrescricaoPam.Pendente: return "Prescrição Pendente";
                case EstadoPrescricaoPam.Atendida: return "Prescrição Atendida";
                case EstadoPrescricaoPam.EmElaboracao: return "Em elaboração";
                case EstadoPrescricaoPam.SemPedido: return "Sem pedido à farmácia";
                default: return "Sem prescrição";
            }
        }

        // DT_SOLSAI_PRO vem "dd/MM/yyyy" ou "dd/MM/yyyy 00:00:00"; HR_SOLSAI_PRO "HH:mm".
        public static bool DataHoraPedido(Solicitacao p, out DateTime val)
        {
            val = DateTime.MinValue;
            string d = (p.Data ?? "").Trim(); if (d.Length > 10) d = d.Substring(0, 10);
            string h = (p.Hora ?? "").Trim(); if (h.Length > 5) h = h.Substring(0, 5);
            return DateTime.TryParseExact(d + " " + h, "dd/MM/yyyy HH:mm",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out val);
        }

        public static string SemAcento(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var norm = s.ToUpperInvariant().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(norm.Length);
            foreach (char c in norm)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            return sb.ToString();
        }
    }
}
