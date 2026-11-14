// Adapter for the pinned 1.4.8 packaged UI. The upstream Rust backend is not
// published. Keep its existing UI ABI; omit all update hooks and release links.
function Kp({title:l="CS2 Bot Improver · 独立版 1.0.0",onSettings:s}) {
  const u=Jr(),f=et();
  return d.jsxs("header",{className:"titlebar","data-tauri-drag-region":true,children:[
    d.jsx("span",{className:"titlebar__title","data-tauri-drag-region":true,children:l}),
    d.jsxs("div",{className:"titlebar__controls",children:[
      d.jsx("button",{className:"tl tl--green",title:f("tb.settings"),"aria-label":f("tb.settings"),onClick:s,children:d.jsx(Rp,{size:12})}),
      d.jsx("button",{className:"tl tl--yellow",title:f("tb.minimize"),"aria-label":f("tb.minimize"),onClick:()=>u.minimize(),children:d.jsx(jp,{size:12})}),
      d.jsx("button",{className:"tl tl--red",title:f("tb.close"),"aria-label":f("tb.close"),onClick:()=>u.close(),children:d.jsx(ed,{size:12})})
    ]})
  ]});
}
