using System.Collections.Generic;
using System.Xml;

namespace SoulmvKit.Core
{
    public class MorphisCommand
    {
        public string Name;
        public Dictionary<string, string> Params = new Dictionary<string, string>();
    }

    public class MorphisRecord
    {
        public string Id;
        public Dictionary<string, string> Items = new Dictionary<string, string>();
        public string Get(string name) { string v; return Items.TryGetValue(name, out v) ? v : null; }
    }

    public class MorphisBlock
    {
        public string Name;
        public string Selected;
        public List<MorphisRecord> Records = new List<MorphisRecord>();
    }

    public class MorphisList
    {
        public string Name;
        public string Title;
        public List<MorphisRecord> Records = new List<MorphisRecord>();
    }

    // Resposta do protocolo Morphis já parseada (ignora namespaces SOAP).
    public class MorphisResponse
    {
        public string Raw;
        public XmlDocument Doc;
        public string Outcome;   // OK / ERROR
        public string Task;      // task GUID do contexto atual
        public string Block;
        public string Item;
        public string ReqId;     // <request reqId="..."/> — usar no próximo monitorInfo
        public List<MorphisCommand> Commands = new List<MorphisCommand>();
        public List<string> Messages = new List<string>();
        public Dictionary<string, MorphisBlock> Blocks = new Dictionary<string, MorphisBlock>();
        public Dictionary<string, MorphisList> Lists = new Dictionary<string, MorphisList>();

        public bool IsOk { get { return Outcome == "OK"; } }

        public MorphisBlock Block_(string name) { MorphisBlock b; return Blocks.TryGetValue(name, out b) ? b : null; }
        public MorphisList List_(string name) { MorphisList l; return Lists.TryGetValue(name, out l) ? l : null; }

        public static MorphisResponse Parse(string xml)
        {
            var r = new MorphisResponse();
            r.Raw = xml;
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            r.Doc = doc;

            XmlElement resp = FirstByLocal(doc.DocumentElement, "MessageResponse");
            if (resp != null) r.Outcome = Attr(resp, "outcome");

            XmlElement control = FirstByLocal(doc.DocumentElement, "control");
            if (control != null)
            {
                r.Task = Attr(control, "task");
                r.Block = Attr(control, "block");
                r.Item = Attr(control, "item");
            }

            XmlElement request = FirstByLocal(doc.DocumentElement, "request");
            if (request != null) r.ReqId = Attr(request, "reqId");

            foreach (XmlElement c in AllByLocal(doc.DocumentElement, "command"))
            {
                var cmd = new MorphisCommand();
                cmd.Name = Attr(c, "name");
                foreach (XmlElement p in AllByLocal(c, "param"))
                {
                    string pn = Attr(p, "name");
                    if (pn != null) cmd.Params[pn] = p.InnerText;
                }
                r.Commands.Add(cmd);
            }

            foreach (XmlElement msg in AllByLocal(doc.DocumentElement, "message"))
            {
                string s = Attr(msg, "message_string");
                if (s != null) r.Messages.Add(s);
            }

            // blocos de dados (body): <block name selected><record id><item name>valor</item>
            foreach (XmlElement b in AllByLocal(doc.DocumentElement, "block"))
            {
                string name = Attr(b, "name");
                if (name == null) continue;
                var blk = new MorphisBlock();
                blk.Name = name;
                blk.Selected = Attr(b, "selected");
                foreach (XmlElement rec in DirectByLocal(b, "record"))
                    blk.Records.Add(ParseRecord(rec));
                r.Blocks[name] = blk;
            }

            // listas (LOV): <list name title><data><record id><item>...
            foreach (XmlElement l in AllByLocal(doc.DocumentElement, "list"))
            {
                string name = Attr(l, "name");
                if (name == null) continue;
                var ml = new MorphisList();
                ml.Name = name;
                ml.Title = Attr(l, "title");
                foreach (XmlElement rec in AllByLocal(l, "record"))
                    ml.Records.Add(ParseRecord(rec));
                r.Lists[name] = ml;
            }
            return r;
        }

        private static MorphisRecord ParseRecord(XmlElement rec)
        {
            var r = new MorphisRecord();
            r.Id = Attr(rec, "id");
            foreach (XmlElement it in DirectByLocal(rec, "item"))
            {
                string inm = Attr(it, "name");
                if (inm != null && !r.Items.ContainsKey(inm)) r.Items[inm] = it.InnerText;
            }
            return r;
        }

        public static List<XmlElement> DirectByLocal(XmlNode root, string local)
        {
            var list = new List<XmlElement>();
            foreach (XmlNode n in root.ChildNodes)
            {
                var el = n as XmlElement;
                if (el != null && el.LocalName == local) list.Add(el);
            }
            return list;
        }

        public MorphisCommand Command(string name)
        {
            foreach (var c in Commands) if (c.Name == name) return c;
            return null;
        }

        // ----- helpers de XML por LocalName (ignora prefixos/namespaces) -----
        public static string Attr(XmlElement e, string name)
        {
            if (e == null) return null;
            foreach (XmlAttribute a in e.Attributes)
                if (a.LocalName == name) return a.Value;
            return null;
        }

        public static XmlElement FirstByLocal(XmlNode root, string local)
        {
            if (root == null) return null;
            foreach (XmlNode n in root.ChildNodes)
            {
                var el = n as XmlElement;
                if (el == null) continue;
                if (el.LocalName == local) return el;
                var deep = FirstByLocal(el, local);
                if (deep != null) return deep;
            }
            return null;
        }

        public static List<XmlElement> AllByLocal(XmlNode root, string local)
        {
            var list = new List<XmlElement>();
            Collect(root, local, list);
            return list;
        }

        private static void Collect(XmlNode root, string local, List<XmlElement> acc)
        {
            if (root == null) return;
            foreach (XmlNode n in root.ChildNodes)
            {
                var el = n as XmlElement;
                if (el == null) continue;
                if (el.LocalName == local) acc.Add(el);
                Collect(el, local, acc);
            }
        }
    }
}
