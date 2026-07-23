namespace ChanthraStudio.Services;

/// <summary>
/// JavaScript injected into the embedded WebView2 for teach mode. When enabled,
/// hovering highlights the nearest actionable element and clicking captures a
/// <b>robust</b> selector (posted back via <c>window.chrome.webview.postMessage</c>)
/// instead of the page's default action.
///
/// Hardening notes (the page is untrusted — its attribute values are
/// attacker-controllable): captured attribute values are escaped before they
/// go into a selector, and the C# side additionally gates messages on teach
/// state + origin and clamps lengths. Selector scoring follows the Magnific
/// lesson: a stable <c>id</c> wins, but GUID / <c>scd-</c> / <c>radix-</c> /
/// <c>:r…</c> / long-hex / 4+digit-segment ids are rejected as unstable and we
/// fall back to <c>data-testid</c> → file-input → contenteditable/textbox →
/// <c>aria-label</c> → role+visible-text → a short structural path. Clicking an
/// icon inside a button climbs to the button so the text anchor is captured.
/// </summary>
public static class WebTeachScript
{
    /// <summary>Defines <c>window.__cstudioTeach</c> (idempotent). Run once per page.</summary>
    public const string Define = """
    (function(){
      if (window.__cstudioTeach) return;
      function isStableId(id){
        if(!id) return false;
        if(/^[0-9a-f]{8}-/i.test(id)) return false;
        if(/^(scd-|radix-|headless|:r)/i.test(id)) return false;
        if(/(^|[-_])[0-9a-f]{16,}([-_]|$)/i.test(id)) return false;
        if(/(^|[-_])\d{4,}([-_]|$)/.test(id)) return false;
        return /^[A-Za-z][\w-]*$/.test(id);
      }
      function esc(s){ return (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/[^\w-]/g, function(m){ return '\\' + m; }); }
      function attrEsc(v){ return String(v).replace(/\\/g,'\\\\').replace(/"/g,'\\"'); }
      function visText(el){ return (el.innerText || el.value || '').replace(/\s+/g,' ').trim().slice(0,40); }
      function actionable(el){
        var t=el;
        while(t && t.nodeType===1 && t!==document.body){
          if(t.matches && t.matches('a,button,[role="button"],input,textarea,[contenteditable="true"],[role="textbox"],[role="combobox"],[aria-haspopup]')) return t;
          t=t.parentElement;
        }
        return null;
      }
      function cssPath(el){
        var parts=[], node=el, depth=0;
        while(node && node.nodeType===1 && depth<5){
          if(isStableId(node.id)){ parts.unshift('#'+esc(node.id)); break; }
          var part=node.tagName.toLowerCase();
          var parent=node.parentElement;
          if(parent){
            var sibs=Array.prototype.filter.call(parent.children, function(c){ return c.tagName===node.tagName; });
            if(sibs.length>1) part += ':nth-of-type('+(sibs.indexOf(node)+1)+')';
          }
          parts.unshift(part); node=parent; depth++;
        }
        return parts.join(' > ');
      }
      function selectorFor(el){
        var tag=el.tagName.toLowerCase();
        if(isStableId(el.id)) return {css:'#'+esc(el.id)};
        var testid=el.getAttribute('data-testid')||el.getAttribute('data-test');
        if(testid) return {css:tag+'[data-testid="'+attrEsc(testid)+'"]'};
        if(tag==='input' && (el.getAttribute('type')||'')==='file') return {css:'input[type=file]'};
        if(el.getAttribute('contenteditable')==='true' || el.getAttribute('role')==='textbox') return {css:'[role=textbox][contenteditable=true]'};
        var aria=el.getAttribute('aria-label');
        if(aria) return {css:tag+'[aria-label="'+attrEsc(aria)+'"]'};
        var text=visText(el);
        if((tag==='button'||el.getAttribute('role')==='button') && text) return {css:'button', text:text};
        var role=el.getAttribute('role');
        if(role) return {css:tag+'[role="'+role+'"]', text:(text||undefined)};
        return {css:cssPath(el), text:(text||undefined)};
      }
      function guessKind(el){
        var tag=el.tagName.toLowerCase();
        if(el.getAttribute('contenteditable')==='true' || el.getAttribute('role')==='textbox' || tag==='textarea') return 'Fill';
        if(tag==='input' && (el.getAttribute('type')||'')==='file') return 'Upload';
        if(el.getAttribute('aria-haspopup') || el.getAttribute('role')==='combobox') return 'SelectModel';
        return 'Click';
      }
      var on=false, last=null, lastOutline='';
      function onMove(e){
        if(!on) return;
        var tgt=actionable(e.target)||e.target;
        if(last===tgt) return;
        if(last) last.style.outline=lastOutline;
        last=tgt; lastOutline=last.style.outline||''; last.style.outline='2px solid #e0b341';
      }
      function onClick(e){
        if(!on) return;
        e.preventDefault(); e.stopPropagation();
        var el=actionable(e.target)||e.target, sel=selectorFor(el);
        var msg={ type:'capture', css:sel.css||'', text:sel.text||'', tag:el.tagName.toLowerCase(), kind:guessKind(el), label:(sel.text||el.tagName.toLowerCase()) };
        try { window.chrome.webview.postMessage(JSON.stringify(msg)); } catch(err){}
        return false;
      }
      window.__cstudioTeach={
        enable:function(){ if(on) return; on=true; document.addEventListener('mousemove',onMove,true); document.addEventListener('click',onClick,true); },
        disable:function(){ on=false; document.removeEventListener('mousemove',onMove,true); document.removeEventListener('click',onClick,true); if(last){ last.style.outline=lastOutline; last=null; } }
      };
    })();
    """;

    public const string Enable = "window.__cstudioTeach && window.__cstudioTeach.enable();";
    public const string Disable = "window.__cstudioTeach && window.__cstudioTeach.disable();";
}
