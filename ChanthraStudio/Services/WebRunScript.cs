namespace ChanthraStudio.Services;

/// <summary>
/// JavaScript injected into the embedded WebView2 to <b>execute</b> recipe
/// steps. Every function takes ONE argument object that the C# side passes as a
/// JSON literal (e.g. <c>window.__cstudioRun.fill({"css":"…","value":"…"})</c>),
/// so a page-authored selector string is a runtime value handed to
/// <c>querySelector</c> — it is NEVER string-concatenated into injected code.
/// Each returns a plain object <c>{ok, …}</c> (ExecuteScriptAsync serialises it
/// to JSON for the runner to parse).
///
/// Magnific specifics handled here: the prompt is a contenteditable rich editor
/// (set via focus + execCommand/InputEvent, not <c>.value</c>); the Generate
/// button is disabled (<c>cursor-not-allowed</c>/<c>disabled</c>) until the
/// prompt has text (the runner polls <c>query</c> for <c>enabled</c>); model is
/// a click-to-open dialog then pick-by-text.
/// </summary>
public static class WebRunScript
{
    public const string Define = """
    (function(){
      if (window.__cstudioRun) return;
      function vis(el){ if(!el) return false; var r=el.getBoundingClientRect(); return r.width>0 && r.height>0; }
      function resolve(css, text){
        var nodes;
        try { nodes = Array.prototype.slice.call(document.querySelectorAll(css||'*')); } catch(e){ return null; }
        if(text){ var t=String(text).toLowerCase(); nodes=nodes.filter(function(n){ return (((n.innerText||n.value||'')+'').toLowerCase().indexOf(t)!==-1); }); }
        nodes=nodes.filter(vis);
        return nodes.length?nodes[0]:null;
      }
      function isEnabled(el){
        if(!el) return false;
        if(el.disabled) return false;
        if(el.getAttribute && el.getAttribute('aria-disabled')==='true') return false;
        var cn=(el.className&&el.className.baseVal!==undefined?el.className.baseVal:el.className)||'';
        return !/cursor-not-allowed/.test(cn);
      }
      function setNativeValue(el,value){
        var proto = el.tagName==='TEXTAREA'?window.HTMLTextAreaElement.prototype:window.HTMLInputElement.prototype;
        var d=Object.getOwnPropertyDescriptor(proto,'value');
        if(d&&d.set){ d.set.call(el,value); } else { el.value=value; }
        el.dispatchEvent(new Event('input',{bubbles:true}));
        el.dispatchEvent(new Event('change',{bubbles:true}));
      }
      function fillCE(el,value){
        el.focus();
        var ok=false;
        try{ document.execCommand('selectAll',false,null); ok=document.execCommand('insertText',false,value); }catch(e){ ok=false; }
        if(!ok){ el.textContent=value; el.dispatchEvent(new InputEvent('input',{bubbles:true,data:value,inputType:'insertText'})); }
      }
      window.__cstudioRun={
        query:function(a){ try{ var el=resolve(a.css,a.text); return {ok:!!el, enabled:isEnabled(el)}; }catch(e){ return {ok:false,error:String(e)}; } },
        fill:function(a){ try{ var el=resolve(a.css,a.text); if(!el) return {ok:false,error:'not-found'}; if(el.tagName==='TEXTAREA'||el.tagName==='INPUT') setNativeValue(el,a.value||''); else fillCE(el,a.value||''); return {ok:true}; }catch(e){ return {ok:false,error:String(e)}; } },
        click:function(a){ try{ var el=resolve(a.css,a.text); if(!el) return {ok:false,error:'not-found'}; if(a.requireEnabled&&!isEnabled(el)) return {ok:false,error:'disabled'}; el.scrollIntoView({block:'center'}); el.click(); return {ok:true}; }catch(e){ return {ok:false,error:String(e)}; } },
        openSelect:function(a){ try{ var el=resolve(a.css,a.text); if(!el) return {ok:false,error:'not-found'}; el.scrollIntoView({block:'center'}); el.click(); return {ok:true}; }catch(e){ return {ok:false,error:String(e)}; } },
        pick:function(a){ try{
            // Scope to the LAST visible overlay (the one openSelect just opened),
            // not the first in document order — a canvas app may have other
            // persistent dialogs/menus earlier in the DOM.
            var overlays=Array.prototype.slice.call(document.querySelectorAll('[role=dialog],[role=listbox],[role=menu]')).filter(vis);
            var scope=overlays.length?overlays[overlays.length-1]:document;
            var opts=Array.prototype.slice.call(scope.querySelectorAll('[role=option],[role=menuitem],[role=radio],button,li')).filter(vis);
            var t=String(a.value||'').toLowerCase().trim();
            var norm=function(n){ return (n.innerText||'').toLowerCase().replace(/\s+/g,' ').trim(); };
            var m=opts.filter(function(n){ return norm(n)===t; })[0]          // exact
                 || opts.filter(function(n){ return norm(n).indexOf(t)===0; })[0] // prefix
                 || opts.filter(function(n){ return norm(n).indexOf(t)!==-1; })[0]; // contains
            if(!m) return {ok:false,error:'option-not-found'};
            m.scrollIntoView({block:'center'}); m.click(); return {ok:true};
          }catch(e){ return {ok:false,error:String(e)}; } }
      };
    })();
    """;
}
