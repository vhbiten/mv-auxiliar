using System;

namespace SoulmvKit.Core
{
    // Lançada quando o servidor indica que a sessão autenticada não vale mais
    // (redireciona para a página de login do CAS, devolve HTML no lugar do XML
    // ou responde 401/403). As telas capturam e voltam para o login.
    public class SessaoExpiradaException : Exception
    {
        public SessaoExpiradaException()
            : base("A sessão no servidor expirou. Faça login novamente.") { }
    }
}
