using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Views;

internal static partial class BmlHtmlAdapter
{
    private const string CompatibilityScript = """
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no">
<style id="srdeck-bml-style">
html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden;background:#000;color:#fff}
body{position:relative;width:240px;height:148px;font-family:'Yu Gothic UI','Meiryo',sans-serif;transform-origin:0 0}
body *{position:absolute;box-sizing:border-box}
a,[onclick],button,input,select,[tabindex]{outline:2px solid transparent}
a:focus,[onclick]:focus,button:focus,input:focus,select:focus,[tabindex]:focus{outline-color:#ffd800}
bevent,beitem{display:none}
</style>
<script id="srdeck-bml-compat">
(()=>{
 const local=(u)=>{if(!u)return '';u=String(u);return u.replace(/^arib-(?:dc|dcl):\/\/[^/]*\/?/i,'https://oneseg-bml.local/').replace(/^arib:\/\/[^/]*\/?/i,'https://oneseg-bml.local/');};
 const launch=(u)=>{try{location.href=local(u);return 0}catch(_){return -1}};
 const browserCompat={
  launchDocument:launch, quitDocument:()=>0, getBrowserSupport:()=>1,
  getProgramID:()=>'', getServiceID:()=>'', getCurrentDate:()=>new Date().toISOString().slice(0,10),
  getCurrentTime:()=>new Date().toTimeString().slice(0,8), playRomSound:()=>0,
  getPersistentArray:()=>[], writePersistentArray:()=>0, readPersistentArray:()=>[],
  reloadActiveDocument:()=>{location.reload();return 0}
 };
 if(!window.browser)window.browser=browserCompat; else Object.assign(window.browser,browserCompat);
 window.BML=window.BML||browserCompat;
 if(!('normalStyle' in Element.prototype))Object.defineProperty(Element.prototype,'normalStyle',{get(){return this.style}});
 if(!document.currentEvent)document.currentEvent={type:'',status:0};
 window.BinaryTable=window.BinaryTable||class{
  constructor(path,format){this.rows=[];this.nrow=0;try{const x=new XMLHttpRequest();x.open('GET',local(path),false);x.responseType='arraybuffer';x.send();if(x.status===200){this.rows=[new TextDecoder('shift_jis').decode(x.response)];this.nrow=1}}catch(_){}}
  toArray(start,count){return this.rows.slice(start,start+count)} close(){}
 };
 const rewrite=()=>document.querySelectorAll('[src],[href],[data]').forEach(el=>{
  for(const a of ['src','href','data'])if(el.hasAttribute(a))el.setAttribute(a,local(el.getAttribute(a)));
 });
 const fit=()=>{const root=document.body.firstElementChild;const w=parseFloat(root?.style.width)||240;const h=parseFloat(root?.style.height)||148;document.body.style.width=w+'px';document.body.style.height=h+'px';const z=Math.min(innerWidth/w,innerHeight/h);document.body.style.transform=`scale(${z})`};
 window.addEventListener('DOMContentLoaded',()=>{rewrite();fit();const first=document.querySelector('a,[onclick],button,input,select,[tabindex]');if(first)first.focus()});
 window.addEventListener('resize',fit);
 window.__srdeckRemote=(key,code)=>{
  const target=document.activeElement||document.body;
  const ev=new KeyboardEvent('keydown',{key,bubbles:true,cancelable:true});
  try{Object.defineProperty(ev,'keyCode',{get:()=>code});Object.defineProperty(ev,'which',{get:()=>code})}catch(_){}
  const accepted=target.dispatchEvent(ev)&&window.dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true,cancelable:true}));
  if(key==='Enter'&&target&&typeof target.click==='function')target.click();
  if(accepted&&key.startsWith('Arrow')){
   const nodes=[...document.querySelectorAll('a,[onclick],button,input,select,[tabindex]')].filter(n=>n.getClientRects().length);
   const i=Math.max(0,nodes.indexOf(target)); if(nodes.length)nodes[(i+(key==='ArrowLeft'||key==='ArrowUp'?-1:1)+nodes.length)%nodes.length].focus();
  }
  target.dispatchEvent(new KeyboardEvent('keyup',{key,bubbles:true}));
 };
})();
</script>
""";

    public static byte[] Transform(OneSegBmlResource resource)
    {
        string text = DecodeText(resource.Content, resource.ContentType);
        text = XmlDeclarationRegex().Replace(text, "");
        text = ProcessingInstructionRegex().Replace(text, "");
        text = DoctypeRegex().Replace(text, "");
        text = text.Replace("<![CDATA[", "", StringComparison.Ordinal)
            .Replace("]]>", "", StringComparison.Ordinal);
        text = BmlOpenRegex().Replace(text, match => $"<{match.Groups[1].Value}html");
        text = SelfClosingRegex().Replace(text, match =>
            $"<{match.Groups[1].Value}{match.Groups[2].Value}></{match.Groups[1].Value}>");
        text = text.Replace("type=\"text/ecmascript\"", "type=\"text/javascript\"",
                StringComparison.OrdinalIgnoreCase)
            .Replace("type=\"application/ecmascript\"", "type=\"text/javascript\"",
                StringComparison.OrdinalIgnoreCase);
        text = AribSchemeRegex().Replace(text, match =>
            "https://oneseg-bml.local/" + match.Groups[1].Value.TrimStart('/'));
        if (!text.Contains("<html", StringComparison.OrdinalIgnoreCase))
            text = $"<html><head></head><body><pre>{WebUtility.HtmlEncode(text)}</pre></body></html>";
        int head = text.IndexOf('>', text.IndexOf("<head", StringComparison.OrdinalIgnoreCase));
        if (head >= 0) text = text.Insert(head + 1, CompatibilityScript);
        else
        {
            int html = text.IndexOf('>', text.IndexOf("<html", StringComparison.OrdinalIgnoreCase));
            text = text.Insert(Math.Max(0, html + 1), "<head>" + CompatibilityScript + "</head>");
        }
        return Encoding.UTF8.GetBytes(text);
    }

    public static string RemoteScript(string key)
    {
        (string jsKey, int code) = key switch
        {
            "ArrowUp" => ("ArrowUp", 38), "ArrowDown" => ("ArrowDown", 40),
            "ArrowLeft" => ("ArrowLeft", 37), "ArrowRight" => ("ArrowRight", 39),
            "Enter" => ("Enter", 13), "Backspace" => ("Backspace", 8),
            "Red" => ("F1", 403), "Green" => ("F2", 404),
            "Yellow" => ("F3", 405), "Blue" => ("F4", 406),
            "Digit1" => ("1", 49), "Digit2" => ("2", 50),
            "Digit3" => ("3", 51), "Digit4" => ("4", 52),
            _ => (key, 0)
        };
        return $"window.__srdeckRemote?.('{jsKey}',{code});";
    }

    private static string DecodeText(byte[] content, string contentType)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Match charset = CharsetRegex().Match(contentType);
        if (charset.Success)
        {
            try { return Encoding.GetEncoding(charset.Groups[1].Value.Trim('"','\'')).GetString(content); }
            catch (ArgumentException) { }
        }
        string utf8 = Encoding.UTF8.GetString(content);
        return utf8.Contains('\ufffd') ? Encoding.GetEncoding(932).GetString(content) : utf8;
    }

    [GeneratedRegex(@"<\?xml[^?]*\?>", RegexOptions.IgnoreCase)]
    private static partial Regex XmlDeclarationRegex();
    [GeneratedRegex(@"<\?.*?\?>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ProcessingInstructionRegex();
    [GeneratedRegex(@"<!DOCTYPE.*?>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DoctypeRegex();
    [GeneratedRegex(@"<(/?)bml\b", RegexOptions.IgnoreCase)]
    private static partial Regex BmlOpenRegex();
    [GeneratedRegex(@"<(div|object|p|span|a)([^>]*)/>", RegexOptions.IgnoreCase)]
    private static partial Regex SelfClosingRegex();
    [GeneratedRegex("""arib-(?:dc|dcl)://[^/"']*/?([^"'\s<]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex AribSchemeRegex();
    [GeneratedRegex(@"charset\s*=\s*([^;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRegex();
}
