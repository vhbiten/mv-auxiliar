using System;
using System.Collections.Generic;
using System.Globalization;

namespace SoulmvKit.Core
{
    // Uma linha da análise de estoque da CAF: consumo do período + saldo + cadastro do MV
    // + saldo nos setores de comparação. Tudo somente-leitura (a decisão é do farmacêutico).
    public class ProdutoEstoque
    {
        public int Codigo;
        public string Nome;
        public string Unidade;
        public int Especie;            // 1 = medicamentos, 2 = materiais, 0 = não identificada
        public decimal Consumo;        // o que SAIU da CAF no período (transferências + saídas)
        public decimal Consumo4m;      // o que saiu nos últimos 120 dias (régua longa p/ item de baixo giro)
        public decimal SaldoCaf;       // saldo atual na CAF
        public decimal VlUnitario;     // custo unitário (histórico do MV; 0 = desconhecido)
        public decimal EstoqueMinimo;  // cadastro do MV (0 = sem cadastro)
        public decimal EstoqueMaximo;
        public decimal PontoPedido;
        public string Fornecedor;      // fornecedor da última entrada ("" = desconhecido)
        public DateTime? UltimaEntrada;
        public decimal[] SaldoSetores; // paralelo a AnaliseResultado.SetorCodigos (ausente no relatório = 0)
    }

    // Resultado completo de uma análise (o que a tela mostra e o cache guarda).
    public class AnaliseResultado
    {
        public DateTime Quando;
        public int DiasPeriodo;
        public int[] SetorCodigos;
        public string[] SetorNomes;
        public bool[] SetorOk;         // false = o relatório daquele setor falhou (coluna vira "—")
        public bool CadastroMvOk;      // false = R_PROD_CRIT falhou (mín/PP/fornecedor ausentes)
        // false = algum relatório por espécie falhou, ou a análise é de uma versão que não
        // os gerava: produto fora do R_PROD_CRIT fica com espécie 0 e só aparece com TIPO
        // em "Todos" (ver MarcarEspecie).
        public bool EspeciesOk;
        public List<ProdutoEstoque> Itens = new List<ProdutoEstoque>();
    }

    // Régua e montagem da análise de estoque da CAF para o pedido de compras.
    //
    // A régua segue a metodologia de farmácia hospitalar (CFF/Comfarhosp): um item é crítico
    // quando a cobertura não alcança o próximo ressuprimento. Os DIAS de cada faixa vêm de
    // dados\estoque-regua.txt (EstoqueRegua), calibrados contra o pedido real de agosto/2026.
    // O mínimo/ponto de pedido cadastrado no MV, quando existe, também dispara — é a régua
    // que os próprios farmacêuticos configuraram.
    public static class AnaliseEstoque
    {
        public static double CoberturaCritica { get { return EstoqueRegua.CriticoDias; } }
        public static double CoberturaAtencao { get { return EstoqueRegua.AtencaoDias; } }
        public static double CoberturaAlvo { get { return EstoqueRegua.AlvoDias; } }

        public const int NivelCritico = 0;
        public const int NivelAtencao = 1;
        public const int NivelOk = 2;
        public const int NivelSemGiro = 3;   // nada saiu no período — sem sinal de demanda

        // Cobertura em dias do saldo da CAF no ritmo de saída do período (infinito sem consumo).
        public static double Cobertura(ProdutoEstoque p, int diasPeriodo)
        {
            return PedidoSugestao.Cobertura(p.Consumo, p.SaldoCaf, diasPeriodo);
        }

        // Cobertura para CLASSIFICAR e ORDENAR: sem saída no período, cai na média dos
        // últimos 4 meses (item de baixo giro tem régua longa, não régua nenhuma — caso
        // real: KCl 100ml zerado, sem saída em 30 dias mas 38 em 4 meses, comprado em ago).
        public static double CoberturaEfetiva(ProdutoEstoque p, int diasPeriodo)
        {
            if (p.Consumo > 0) return Cobertura(p, diasPeriodo);
            if (p.Consumo4m > 0) return PedidoSugestao.Cobertura(p.Consumo4m, p.SaldoCaf, 120);
            return double.PositiveInfinity;
        }

        // Classificação. Consumo zero no período E nos 4 meses NÃO vira crítico por cobertura
        // (item de saída esporádica geraria falso alarme — vício conhecido do cálculo por
        // média), mas o mínimo/PP do MV continua valendo: é assim que um item de emergência
        // parado dispara mesmo sem giro. Com giro só nos 4 meses, a média longa pode ESCALAR
        // (crítico/atenção) mas nunca rebaixa a OK — sem saída recente não há folga provada.
        public static int Nivel(ProdutoEstoque p, int diasPeriodo)
        {
            bool temMin = p.EstoqueMinimo > 0;
            bool temPP = p.PontoPedido > 0;
            if (p.Consumo <= 0)
            {
                if (temMin && p.SaldoCaf < p.EstoqueMinimo) return NivelCritico;
                if (temPP && p.SaldoCaf <= p.PontoPedido) return NivelAtencao;
                if (p.Consumo4m > 0)
                {
                    double cob120 = CoberturaEfetiva(p, diasPeriodo);
                    if (p.SaldoCaf <= 0 || cob120 < CoberturaCritica) return NivelCritico;
                    if (cob120 < CoberturaAtencao) return NivelAtencao;
                }
                return NivelSemGiro;
            }
            double cob = Cobertura(p, diasPeriodo);
            if (p.SaldoCaf <= 0) return NivelCritico;
            if (cob < CoberturaCritica) return NivelCritico;
            if (temMin && p.SaldoCaf < p.EstoqueMinimo) return NivelCritico;
            if (cob < CoberturaAtencao) return NivelAtencao;
            if (temPP && p.SaldoCaf <= p.PontoPedido) return NivelAtencao;
            return NivelOk;
        }

        // Sugestão de compra (modelo de programação do CFF): repor até a cobertura-alvo,
        // arredondando para embalagem fechada quando cadastrada. A régua do CADASTRO anda
        // junto: se o item está abaixo do mínimo (ou no ponto de pedido) do MV, o alvo sobe
        // para o do cadastro (máximo; sem máximo, o dobro do mínimo/PP) — senão um item que
        // o próprio Nivel marca como Crítico sairia com sugestão 0 no CSV, e o comprador
        // tenderia a pular exatamente o item marcado. Sem consumo e sem cadastro: 0, a
        // palavra é do farmacêutico.
        public static int Sugestao(ProdutoEstoque p, int diasPeriodo)
        {
            int caixa = PedidoEmbalagens.Obter(p.Codigo);
            decimal alvo = 0;
            if (p.Consumo > 0)
            {
                decimal diario = p.Consumo / Math.Max(1, diasPeriodo);
                alvo = diario * (decimal)CoberturaAlvo;
            }
            else if (p.Consumo4m > 0)
            {
                // Baixo giro: repõe pela média dos 4 meses (mesma régua da classificação).
                alvo = (p.Consumo4m / 120m) * (decimal)CoberturaAlvo;
            }
            if ((p.EstoqueMinimo > 0 && p.SaldoCaf < p.EstoqueMinimo) ||
                (p.PontoPedido > 0 && p.SaldoCaf <= p.PontoPedido))
            {
                decimal ac = AlvoCadastro(p);
                if (ac > alvo) alvo = ac;
            }
            decimal falta = alvo - p.SaldoCaf;
            if (falta <= 0) return 0;
            return Fechar(falta, caixa);
        }

        // Até onde repor segundo o cadastro do MV: o máximo; sem máximo, o dobro do
        // mínimo (ou do ponto de pedido) — a mesma folga que o Kanban do MV usa.
        private static decimal AlvoCadastro(ProdutoEstoque p)
        {
            if (p.EstoqueMaximo > 0) return p.EstoqueMaximo;
            if (p.EstoqueMinimo > 0) return p.EstoqueMinimo * 2;
            if (p.PontoPedido > 0) return p.PontoPedido * 2;
            return 0;
        }

        private static int Fechar(decimal qtd, int caixa)
        {
            if (qtd <= 0) return 0;
            if (caixa > 1)
            {
                if (qtd > 10000000) qtd = 10000000;
                long caixas = (long)Math.Ceiling(qtd / caixa);
                if (caixas < 1) caixas = 1;
                long v = caixas * caixa;
                return v > int.MaxValue ? int.MaxValue : (int)v;
            }
            return PedidoSugestao.ArredondarBonito(qtd);
        }

        // Junta os relatórios numa lista única por produto. Regras de ausência:
        //  - só na movimentação: saldo CAF = "Qtd Atual" do próprio relatório;
        //  - só na posição (sem giro no período): consumo 0, saldo da posição;
        //  - ausente do relatório de um setor = saldo 0 lá (o relatório lista todo saldo != 0);
        //  - R_PROD_CRIT enriquece (mín/máx/PP, fornecedor, espécie) e SÓ cria linha nova
        //    para item ativo com mínimo/PP cadastrado que não veio nos outros relatórios
        //    (ruptura prolongada: zerado e sem giro — o resto do universo dele não é
        //    decisão de compra por consumo).
        //
        // A movimentação vem de TODAS as espécies num relatório só (a CAF movimenta 12
        // espécies, não só medicamento/material — separar por espécie deixaria as demais
        // com "sem giro" falso). A espécie de cada produto vem do cadastro (R_PROD_CRIT)
        // e, para quem ele não lista, de especiePorCodigo (ver MarcarEspecie); quem não
        // está em nenhum dos dois fica com espécie 0 e só aparece no filtro "Todos".
        //
        // movimentacao4m: R_MOVI_ESTOQ dos últimos 120 dias, com dois papéis (calibrados
        // contra o pedido de compras real de agosto/2026):
        //  - COMPLETA O UNIVERSO: item que saiu nos 4 meses mas não no período e está
        //    zerado não vem em nenhum outro relatório — e foi comprado na prática;
        //  - REGRA DO DESCONTINUADO (do farmacêutico): sem movimento nos 4 meses o item
        //    sai da análise, EXCETO se tem saldo (segue visível em "Sem giro" — sondas de
        //    baixo giro compradas em agosto provaram) ou se está abaixo do mínimo de um
        //    cadastro VIVO (última entrada no último ano — o gel PHMB comprado em agosto
        //    tinha entrada de out/2025; o cadastro morto de 2023 continua fora).
        // null = regra e complemento desligados.
        // EstoqueExcluidos corta ainda: lista manual (itens por paciente) e o marcador
        // "(NÃO PADRÃO)" no nome — o flag do cadastro é filtrado na origem, nos relatórios.
        public static AnaliseResultado Montar(
            List<MovProduto> movimentacao,
            List<SaldoProduto> posicaoCaf, List<ProdCritItem> cadastroMv,
            int[] setorCodigos, string[] setorNomes, List<SaldoProduto>[] posicaoSetores,
            bool[] setorOk, int diasPeriodo, List<MovProduto> movimentacao4m,
            Dictionary<int, int> especiePorCodigo)
        {
            var r = new AnaliseResultado();
            r.Quando = DateTime.Now;
            r.DiasPeriodo = diasPeriodo;
            r.SetorCodigos = setorCodigos;
            r.SetorNomes = setorNomes;
            r.SetorOk = setorOk;
            r.CadastroMvOk = cadastroMv != null;
            r.EspeciesOk = especiePorCodigo != null;

            int nSet = setorCodigos.Length;
            var porCodigo = new Dictionary<int, ProdutoEstoque>();

            AcumularMov(porCodigo, movimentacao, 0, nSet);

            if (posicaoCaf != null)
            {
                var naPosicao = new HashSet<int>();
                foreach (var s in posicaoCaf)
                {
                    naPosicao.Add(s.Codigo);
                    ProdutoEstoque p;
                    if (porCodigo.TryGetValue(s.Codigo, out p))
                    {
                        // A posição é emitida DEPOIS da movimentação: o saldo dela é mais novo.
                        p.SaldoCaf = s.Saldo;
                    }
                    else
                    {
                        p = Novo(s.Codigo, s.Nome, s.Unidade, 0, nSet);
                        p.SaldoCaf = s.Saldo;
                        porCodigo[s.Codigo] = p;
                    }
                }
                // A posição lista todo saldo != 0. Quem movimentou mas NÃO está nela zerou
                // entre um relatório e outro — manter o "Qtd Atual" da movimentação seria
                // ficar com o único saldo mais VELHO do merge.
                foreach (var p in porCodigo.Values)
                    if (!naPosicao.Contains(p.Codigo)) p.SaldoCaf = 0;
            }

            if (movimentacao4m != null)
                foreach (var m in movimentacao4m)
                {
                    ProdutoEstoque p;
                    if (porCodigo.TryGetValue(m.Codigo, out p))
                    {
                        p.Consumo4m = m.Consumo;
                        if (p.VlUnitario <= 0 && m.VlUnitario > 0) p.VlUnitario = m.VlUnitario;
                    }
                    else
                    {
                        // Saiu nos 4 meses, nada no período e zerado (ausente da posição):
                        // invisível para os outros relatórios — e caso real de compra.
                        p = Novo(m.Codigo, m.Nome, m.Unidade, 0, nSet);
                        p.SaldoCaf = m.EstoqueAtual;
                        p.Consumo4m = m.Consumo;
                        p.VlUnitario = m.VlUnitario;
                        porCodigo[m.Codigo] = p;
                    }
                }

            if (cadastroMv != null)
                foreach (var c in cadastroMv)
                {
                    ProdutoEstoque p;
                    if (!porCodigo.TryGetValue(c.Codigo, out p))
                    {
                        // Sem movimento no período E sem saldo (ausente dos dois relatórios),
                        // mas com mínimo/PP cadastrado: é a RUPTURA PROLONGADA — o caso mais
                        // perigoso não pode ser o único que some da análise. Inativos ficam
                        // de fora (item descontinuado zerado não é pendência de compra).
                        if (c.Inativo || (c.EstoqueMinimo <= 0 && c.PontoPedido <= 0)) continue;
                        p = Novo(c.Codigo, c.Nome, c.Unidade, c.Especie, nSet);
                        p.SaldoCaf = c.EstoqueAtual;
                        porCodigo[c.Codigo] = p;
                    }
                    p.EstoqueMinimo = c.EstoqueMinimo;
                    p.EstoqueMaximo = c.EstoqueMaximo;
                    p.PontoPedido = c.PontoPedido;
                    p.Fornecedor = c.Fornecedor ?? "";
                    p.UltimaEntrada = c.UltimaEntrada;
                    if (p.Especie == 0 && c.Especie > 0) p.Especie = c.Especie;
                    if (p.VlUnitario <= 0 && c.CustoMedio > 0) p.VlUnitario = c.CustoMedio;
                }

            for (int i = 0; i < nSet; i++)
            {
                if (posicaoSetores[i] == null) continue;
                foreach (var s in posicaoSetores[i])
                {
                    ProdutoEstoque p;
                    if (porCodigo.TryGetValue(s.Codigo, out p)) p.SaldoSetores[i] = s.Saldo;
                    // Produto que só existe num setor (nunca passou pela CAF no recorte) fica
                    // fora: a análise é do que a CAF compra e distribui.
                }
            }

            // Espécie de quem o R_PROD_CRIT não conhece. Só preenche o que ficou em 0:
            // quando as duas fontes têm o produto, dizem a mesma coisa (é o mesmo
            // CD_ESPECIE do cadastro).
            if (especiePorCodigo != null)
                foreach (var p in porCodigo.Values)
                {
                    int e;
                    if (p.Especie == 0 && especiePorCodigo.TryGetValue(p.Codigo, out e)) p.Especie = e;
                }

            // Cortes finais (na ordem: primeiro a regra objetiva, depois as listas).
            int fora4m = 0, foraExcluidos = 0;
            foreach (var p in porCodigo.Values)
            {
                if (movimentacao4m != null && p.Consumo <= 0 && p.Consumo4m <= 0 && p.SaldoCaf <= 0
                    && !CadastroVivoAbaixoDoMinimo(p))
                { fora4m++; continue; }
                if (EstoqueExcluidos.Excluido(p.Codigo, p.Nome))
                { foraExcluidos++; continue; }
                r.Itens.Add(p);
            }
            if (fora4m > 0 || foraExcluidos > 0)
                Logger.Log("Análise de estoque: " + fora4m + " item(ns) fora por 4 meses sem movimento; "
                    + foraExcluidos + " pela lista de excluídos/marcador de não-padrão.");

            // Ordem alfabética pelo nome (pedido do dono em 04/09/2026; até então era por
            // criticidade: críticos primeiro, menor cobertura primeiro). A Situação segue
            // visível na coluna própria e o filtro de SITUAÇÃO faz o recorte, então a
            // lista lê como o catálogo que o comprador conhece. O RenderGrid reordena de
            // novo, porque um cache salvo pela versão anterior vem na ordem velha.
            r.Itens.Sort(CompararNomes);
            return r;
        }

        private static readonly CompareInfo ComparadorPt = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;

        // Alfabética em pt-BR sem distinguir maiúscula nem acento ("ÁCIDO" fica junto de
        // "ACIDO"); empate pelo código, para a ordem ser estável entre gerações.
        public static int CompararNomes(ProdutoEstoque a, ProdutoEstoque b)
        {
            int c = ComparadorPt.Compare(a.Nome ?? "", b.Nome ?? "",
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
            return c != 0 ? c : a.Codigo.CompareTo(b.Codigo);
        }

        // Mapa código → espécie a partir dos relatórios FILTRADOS por espécie (o R_MOVI_ESTOQ
        // dos 4 meses e o R_POS_EST_S da CAF, gerados uma vez para medicamentos e uma para
        // materiais). Existe porque o R_PROD_CRIT, única fonte de espécie até 04/09/2026, só
        // lista produto com classificação ABC/XYZ (é assim que o manual do MV o descreve):
        // na análise real de 02/09/2026, 148 dos 764 produtos ficavam com espécie 0 —
        // Regencel, Reparil, fluoruracila, paclitaxel, luvas, seringas — e sumiam do filtro
        // "Medicamentos"/"Materiais". O filtro CD_ESPECIE desses relatórios lê o CD_ESPECIE
        // do cadastro, que todo produto tem. O movimento de 4 meses cobre quem zerou (a
        // posição não lista saldo 0) e a posição cobre quem tem saldo mas não gira: juntos,
        // todo o universo da análise. O primeiro a chegar vale (mesma fonte, mesmo valor).
        public static void MarcarEspecie(Dictionary<int, int> mapa, int especie,
            List<MovProduto> movimentacao, List<SaldoProduto> posicao)
        {
            if (mapa == null || especie <= 0) return;
            if (movimentacao != null)
                foreach (var m in movimentacao)
                    if (!mapa.ContainsKey(m.Codigo)) mapa[m.Codigo] = especie;
            if (posicao != null)
                foreach (var s in posicao)
                    if (!mapa.ContainsKey(s.Codigo)) mapa[s.Codigo] = especie;
        }

        // "Cadastro vivo" = mínimo/PP configurado, saldo abaixo dele e ÚLTIMA ENTRADA no
        // último ano. Distingue o gel PHMB (entrada out/2025, comprado em agosto) do
        // nintedanibe (entrada 2023, ninguém compra): o primeiro fica, o segundo sai.
        private static bool CadastroVivoAbaixoDoMinimo(ProdutoEstoque p)
        {
            bool abaixo = (p.EstoqueMinimo > 0 && p.SaldoCaf < p.EstoqueMinimo) ||
                          (p.PontoPedido > 0 && p.SaldoCaf <= p.PontoPedido);
            if (!abaixo) return false;
            if (!p.UltimaEntrada.HasValue) return false;
            return (DateTime.Today - p.UltimaEntrada.Value).TotalDays <= 365;
        }

        private static void AcumularMov(Dictionary<int, ProdutoEstoque> mapa, List<MovProduto> mov, int especie, int nSet)
        {
            if (mov == null) return;
            foreach (var m in mov)
            {
                ProdutoEstoque p;
                if (!mapa.TryGetValue(m.Codigo, out p))
                {
                    p = Novo(m.Codigo, m.Nome, m.Unidade, especie, nSet);
                    mapa[m.Codigo] = p;
                }
                p.Consumo += m.Consumo;
                p.SaldoCaf = m.EstoqueAtual;
                if (m.VlUnitario > 0) p.VlUnitario = m.VlUnitario;
                if (p.Especie == 0) p.Especie = especie;
            }
        }

        private static ProdutoEstoque Novo(int codigo, string nome, string unidade, int especie, int nSet)
        {
            var p = new ProdutoEstoque();
            p.Codigo = codigo;
            p.Nome = nome ?? "";
            p.Unidade = unidade ?? "";
            p.Especie = especie;
            p.Fornecedor = "";
            p.SaldoSetores = new decimal[nSet];
            return p;
        }

        public static string NomeNivel(int nivel)
        {
            if (nivel == NivelCritico) return "Crítico";
            if (nivel == NivelAtencao) return "Atenção";
            if (nivel == NivelOk) return "OK";
            return "Sem giro";
        }

        // A exportação CSV para o setor de compras existiu aqui e foi APOSENTADA a pedido
        // do farmacêutico (23/08/2026) — a decisão de compra acontece olhando a grade.
        // Se voltar, lembrar do que a versão antiga tratava: separador ';' + vírgula
        // decimal + UTF-8 com BOM (Excel pt-BR) e apóstrofo em campo começando com
        // = + - @ (injeção de fórmula).
    }
}
