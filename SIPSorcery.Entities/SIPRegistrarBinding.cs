//------------------------------------------------------------------------------
// <auto-generated>
//    This code was generated from a template.
//
//    Manual changes to this file may cause unexpected behavior in your application.
//    Manual changes to this file will be overwritten if the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace SIPSorcery.Entities
{
    using System;
    using System.Collections.Generic;
    
    public partial class SIPRegistrarBinding
    {
        public string AdminMemberID { get; set; }
        public string ContactURI { get; set; }
        public int Expiry { get; set; }
        public string ExpiryTime { get; set; }
        public string ID { get; set; }
        public string LastUpdate { get; set; }
        public string MangledContactURI { get; set; }
        public string Owner { get; set; }
        public string ProxySIPSocket { get; set; }
        public string RegistrarSIPSocket { get; set; }
        public string RemoteSIPSocket { get; set; }
        public string SIPAccountID { get; set; }
        public string SIPAccountName { get; set; }
        public string UserAgent { get; set; }
    
        public virtual SIPAccount sipaccount { get; set; }
    }
}
