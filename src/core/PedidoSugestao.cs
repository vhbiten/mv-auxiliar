using System;

namespace SoulmvKit.Core
{
    // Sugestão de quantidade do pré-pedido à CAF — política de REVISÃO PERIÓDICA por
    // COBERTURA (padrão em farmácia hospitalar):
    //
    //   consumo diário = consumo do período / dias do período
    //   cobertura      = estoque atual / consumo diário   (em dias)
    //
    //   cobertura >= LIMIAR  ->  sugestão 0 (estoque folgado: fora do pedido)
    //   cobertura <  LIMIAR  ->  repõe o que saiu, arredondado p/ número de caixa fechada
    //                            (base-stock: pedir o consumido devolve o estoque ao nível
    //                            de trabalho; foi o padrão observado no pedido real)
    //
    // LIMIAR calibrado contra um pedido REAL dos farmacêuticos (55 itens / 114 produtos):
    // 12 dias reproduzia 93/114 decisões de incluir/pular (82%) — o melhor entre 7..20 dias.
    // Quem fica de fora continua na grade (com a cobertura visível) para inclusão manual;
    // estoque NEGATIVO tem cobertura 0 e sempre entra. A palavra final é do usuário.
    //
    // DIAS DO PERÍODO (26/11/2026): o relatório vai de "hoje - N dias, 00:00" ATÉ A HORA DA
    // GERAÇÃO, então a janela real é N dias + a parte de hoje já corrida (ver JanelaDias).
    // A conta dividia por N cheio e o consumo diário saía inflado — 15% na mediana e até
    // 34% nos 38 pedidos de 01/10 a 25/11/2026 — com a cobertura dependendo da HORA em que
    // o pedido era gerado. O limiar de 12 tinha sido calibrado com essa conta inflada; com
    // a janela real o equivalente é 14 (12 × 1,15). Conferido no pedido de materiais
    // nº 494846 (25/11, 121 produtos): janela real + 14 repete a decisão enviada em 118,
    // o mesmo que a conta antiga; janela real + 12 cairia para 111 (7 produtos que o
    // farmacêutico pediu sairiam da sugestão).
    public static class PedidoSugestao
    {
        public const double CoberturaLimiarDias = 14.0;

        // Mantissas "bonitas" (inclui dúzias 12/24, meia-centena, 75): escaladas ×10
        // a cada década. Ex.: 10,12,15,20,24,25,30,40,50,60,75,80,100,120,150...
        private static readonly int[] Base = { 10, 12, 15, 20, 24, 25, 30, 40, 50, 60, 75, 80 };

        // Quantos dias o relatório de movimentações cobre DE VERDADE: do início (00:00 de
        // N dias atrás) até a hora em que foi gerado. Nunca menos que os N dias escolhidos
        // (relógio da estação errado não pode encolher a janela e inflar o consumo).
        public static double JanelaDias(DateTime inicio, DateTime geradoEm, int diasEscolhidos)
        {
            double real = (geradoEm - inicio).TotalDays;
            double minimo = Math.Max(1, diasEscolhidos);
            if (double.IsNaN(real) || real < minimo) return minimo;
            // Teto de sanidade: a janela não passa de N dias + o dia de hoje inteiro.
            return real > minimo + 1 ? minimo + 1 : real;
        }

        // Cobertura em DIAS do estoque atual frente ao consumo do período.
        // Sem consumo -> infinito (não há sinal de demanda).
        // diasPeriodo aceita fração (a janela real do pré-pedido); a análise de compras da
        // CAF continua passando dias inteiros (30/120), onde a parte de hoje não pesa.
        public static double Cobertura(decimal consumo, decimal estoqueAtual, double diasPeriodo)
        {
            if (consumo <= 0) return double.PositiveInfinity;
            double diario = (double)consumo / Math.Max(1.0, diasPeriodo);
            double estoque = (double)estoqueAtual;
            if (estoque <= 0) return 0;
            return estoque / diario;
        }

        // caixaFechada: quantos vêm na embalagem (cadastro do usuário; 0/1 = sem cadastro).
        // Com caixa, a sugestão é o MENOR número de caixas fechadas que cobre o consumo
        // (16 comprimidos consumidos, caixa de 30 → 1 caixa = 30): para a CAF é muito
        // melhor separar caixa inteira do que abrir e contar picado.
        public static int Sugerir(decimal consumo, decimal estoqueAtual, double diasPeriodo, int caixaFechada)
        {
            if (consumo <= 0) return 0;
            if (Cobertura(consumo, estoqueAtual, diasPeriodo) >= CoberturaLimiarDias) return 0;
            if (caixaFechada > 1)
            {
                if (consumo > 10000000) consumo = 10000000;   // mesmo teto sanitário do arredondamento
                long caixas = (long)Math.Ceiling(consumo / caixaFechada);
                if (caixas < 1) caixas = 1;
                long v = caixas * caixaFechada;
                return v > int.MaxValue ? int.MaxValue : (int)v;
            }
            return ArredondarBonito(consumo);
        }

        // O MENOR número "bonito" (fácil de separar em caixa fechada) >= o consumo.
        // Ex.: 37→40, 53→60, 202→240, 350→400, 997→1000, 12→12.
        public static int ArredondarBonito(decimal consumo)
        {
            if (consumo <= 0) return 0;
            if (consumo > 10000000) consumo = 10000000;   // teto sanitário: evita overflow c/ PDF absurdo
            int c = (int)Math.Ceiling(consumo);
            if (c <= 6) return c;      // muito pouco: pede o próprio consumo
            if (c <= 8) return 8;
            long escala = 1;
            while (escala <= 10000000)
            {
                foreach (int b in Base)
                {
                    long v = (long)b * escala;
                    if (v >= c) return (int)v;
                }
                escala *= 10;
            }
            return c;   // consumo astronômico: devolve como veio
        }
    }
}
