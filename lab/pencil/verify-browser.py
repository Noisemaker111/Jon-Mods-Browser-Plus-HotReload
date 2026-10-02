"""Drive the local editor in a tab in Jon's already-approved running Chrome.

No browser launch, profile copy or game launch. Default makes no source writes.
--save temporarily exercises the real save button, then restores exact original
mod/draft bytes. Uses Errand's existing holder only.
"""
import json
import sys
import time
from pathlib import Path
import hashlib
import argparse

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import server
from errand.chrome_holder import holder_file
from errand.chrome_link import ChromeLink
from errand.engines.chrome import Tab

parser = argparse.ArgumentParser()
parser.add_argument('--save', action='store_true', help='Verify the actual XML-save button; restore original bytes afterwards')
args = parser.parse_args()

if json.loads(holder_file().read_text())["state"] != "connected":
    raise RuntimeError("Approve Errand's connection to the running Chrome first")
link = ChromeLink({})
conn = link.get(wait=3)
target = conn.send("Target.createTarget", {"url": "about:blank", "background": True}, deadline=time.time() + 10)["targetId"]
session = conn.send("Target.attachToTarget", {"targetId": target, "flatten": True}, deadline=time.time() + 10)["sessionId"]
tab = Tab(conn, target, session, time.time() + 180)
out = server.workspace() / "verification"
out.mkdir(exist_ok=True)
key = 'XUi_InGame:templates.xml:party_entry'
draft_path = server.workspace() / 'studio/drafts' / (hashlib.sha256(key.encode()).hexdigest() + '.json')
draft_before = draft_path.read_bytes() if draft_path.exists() else None
mod_path = server.ROOT / 'mods/JonPartyPortraits/Config/XUi_InGame/templates.xml'
mod_before = mod_path.read_bytes()
restore_mod_after = None


def export_tree():
    return tab.evaluate("""new Promise((resolve,reject) => {
      const frame=document.querySelector('#studio-pencil'), request=crypto.randomUUID();
      const timer=setTimeout(()=>reject(new Error('Export response timeout')),10000);
      const receive=event=> { if(event.source!==frame.contentWindow||event.data?.request!==request)return;
        clearTimeout(timer);window.removeEventListener('message',receive);
        event.data.error?reject(new Error(event.data.error)):resolve(event.data.result); };
      window.addEventListener('message',receive);
      frame.contentWindow.postMessage({channel:'7days-pencil',action:'export',request},location.origin);
    })""")


def text_node(tree):
    if tree['attrs'].get('name') == 'TextContent':
        return tree
    for child in tree['children']:
        found = text_node(child)
        if found:
            return found

try:
    tab.cdp("Page.enable")
    tab.cdp("Page.setLifecycleEventsEnabled", enabled=True)
    tab.cdp("Emulation.setDeviceMetricsOverride", width=1600, height=1100, deviceScaleFactor=1, mobile=False)
    tab.cdp("Emulation.setFocusEmulationEnabled", enabled=True)
    # Background Chrome tabs only paint while a screencast is requested.
    tab.screenshot()
    tab.navigate("http://127.0.0.1:7777/#ui")
    result = tab.evaluate("""new Promise(resolve => {
      const status = document.querySelector('#studio-status');
      const done = () => { observer.disconnect(); clearTimeout(timer); resolve(status.innerText); };
      const observer = new MutationObserver(() => { if (/loaded|Recovered|failed|required|respond/i.test(status.innerText)) done(); });
      observer.observe(status, {childList:true,subtree:true}); const timer = setTimeout(done, 25000);
      if (/loaded|Recovered|failed|required/i.test(status.innerText)) done();
    })""")
    diagnostics = tab.evaluate("""(() => {
      const f = document.querySelector('#studio-pencil');
      return {status:document.querySelector('#studio-status').innerText, notice:document.querySelector('#notice').innerText,
        frameText:f.contentDocument?.body.innerText,
        resources:f.contentWindow.performance.getEntriesByType('resource').filter(r=>r.name.includes('wasm')||r.name.includes('ttf')).map(r=>({name:r.name,size:r.transferSize,status:r.responseStatus}))};
    })()""")
    print(json.dumps(diagnostics, indent=2), flush=True)
    (out / "pencil-browser.json").write_text(json.dumps(diagnostics, indent=2), encoding="utf-8")
    shot = tab.screenshot()
    if shot:
        (out / "pencil-workspace.jpg").write_bytes(shot)
    assert "loaded" in result.lower() or "Recovered" in result, "Game scene did not finish loading"
    assert "TextContent" in diagnostics["frameText"], "Native layers not visible in full editor"
    assert "Aborted" not in diagnostics["frameText"], "CanvasKit failed"
    before = export_tree()
    point = tab.evaluate("""(() => {
      const frame=document.querySelector('#studio-pencil'), input=frame.contentDocument.querySelector('[aria-label="Font size"]');
      if(!input)throw new Error('Font size property missing');input.scrollIntoView({block:'center'});
      const r=input.getBoundingClientRect(),f=frame.getBoundingClientRect();return {x:f.x+r.x+r.width/2,y:f.y+r.y+r.height/2};
    })()""")
    tab.cdp('Input.dispatchMouseEvent', type='mousePressed', button='left', clickCount=2, **point)
    tab.cdp('Input.dispatchMouseEvent', type='mouseReleased', button='left', clickCount=2, **point)
    tab.cdp('Input.dispatchKeyEvent', type='keyDown', key='a', code='KeyA', modifiers=2)
    tab.cdp('Input.dispatchKeyEvent', type='keyUp', key='a', code='KeyA', modifiers=2)
    tab.cdp('Input.insertText', text='24')
    tab.cdp('Input.dispatchKeyEvent', type='keyDown', key='Enter', code='Enter')
    tab.cdp('Input.dispatchKeyEvent', type='keyUp', key='Enter', code='Enter')
    edited = export_tree()
    assert text_node(edited)['attrs']['font_size'] == '24', 'Real property edit did not reach XML adapter'
    assert text_node(edited)['attrs']['text'] == text_node(before)['attrs']['text'], 'A font edit baked the preview binding into XML'
    if args.save:
        assert mod_path.read_bytes() == mod_before, 'Source changed during verification; refusing to overwrite'
        tab.evaluate("""(() => {
          const original=window.fetch;
          window.__saveResult=new Promise(resolve=> {
            window.fetch=async (...args)=> { const response=await original(...args);
              if(args[0]==='/api/studio/save')resolve(await response.clone().json());return response; };
          });
          document.querySelector('#studio-save').scrollIntoView({block:'center'});
        })()""")
        save_point = tab.evaluate("(() => { const r=document.querySelector('#studio-save').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}; })()")
        tab.cdp('Input.dispatchMouseEvent', type='mousePressed', button='left', clickCount=1, **save_point)
        tab.cdp('Input.dispatchMouseEvent', type='mouseReleased', button='left', clickCount=1, **save_point)
        saved = tab.evaluate('window.__saveResult')
        if 'scene' in saved:
            restore_mod_after = mod_path.read_bytes()
        assert 'scene' in saved, saved
        assert (server.workspace() / saved['backup']).read_bytes() == mod_before, 'Backup is not byte-exact'
        assert text_node(saved['scene']['tree'])['attrs']['font_size'] == '24', 'Actual XML source save lost the property edit'
        assert text_node(saved['scene']['tree'])['attrs']['text'] == text_node(before)['attrs']['text'], 'Actual save replaced native binding'
        print('PASS actual browser Save to mod XML button, real source reload, exact backup; original bytes restored afterwards', flush=True)
    else:
        tab.evaluate("document.querySelector('#studio-pencil').contentDocument.activeElement.blur()")
        tab.cdp('Input.dispatchKeyEvent', type='keyDown', key='z', code='KeyZ', modifiers=2)
        tab.cdp('Input.dispatchKeyEvent', type='keyUp', key='z', code='KeyZ', modifiers=2)
        assert export_tree() == before, 'OpenPencil undo did not restore the native tree'
        assert mod_path.read_bytes() == mod_before, 'Browser verification changed mod XML without a save'
    design = tab.evaluate("""new Promise(resolve => {
      const frame=document.querySelector('#design-pencil');
      const timer=setTimeout(()=>resolve('Design editor load timed out'),15000);
      frame.addEventListener('load',()=> {
        const check=()=> { const text=frame.contentDocument?.body.innerText||'';
          if(text.includes('Layers') && frame.contentDocument.querySelectorAll('canvas').length) {
            observer.disconnect();clearTimeout(timer);resolve(text);
          } };
        const observer=new MutationObserver(check);observer.observe(frame.contentDocument.body,{childList:true,subtree:true});check();
      },{once:true});location.hash='#board';
    })""")
    assert 'Layers' in design and 'Design' in design, 'Unrestricted design board is not the full editor'
    design_shot = tab.screenshot()
    if design_shot:
        (out / 'pencil-design-workspace.jpg').write_bytes(design_shot)
    assert tab.evaluate("document.querySelector('#world').options.length") > 0, 'Studio startup blocked world/map initialization'
    print("PASS full local editor: Chrome embed, native layers, actual property edit, binding preservation and screenshot", flush=True)
finally:
    conn.send("Target.closeTarget", {"targetId": target}, deadline=time.time() + 10)
    if restore_mod_after is not None:
        if mod_path.read_bytes() != restore_mod_after:
            raise RuntimeError('Source changed externally after the test save; original backup preserved, refusing to discard other work')
        mod_path.write_bytes(mod_before)
    # Private draft timers may have fired while driving the real property panel.
    # Preserve the user's exact prior recovery copy, including absent drafts.
    if draft_before is None:
        draft_path.unlink(missing_ok=True)
    else:
        draft_path.write_bytes(draft_before)
