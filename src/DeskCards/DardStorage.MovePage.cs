namespace DeskCards;

/// <summary>
/// 저장소를 옮기는 페이지. 카드와 같은 origin에서 열려야 그 저장소를 읽고 쓸 수 있으므로, 관리용 화면이 그 주소로 이 페이지를 연다.
/// 내보내기: localStorage·IndexedDB·OPFS를 읽어 조각(데이터는 원래 바이트 그대로, 구조는 index.json)을 /__desk/put으로 보낸다.
/// 가져오기: /__desk/get으로 같은 조각을 받아 그대로 다시 만든다. 큰 파일은 8MB씩 나눠 보낸다.
/// IndexedDB 값은 구조화 복제 값이라 JSON에 없는 것(Date, Map, Blob, ArrayBuffer 등)은 표시를 붙여 담는다. 옮길 수 없는 값(CryptoKey 등)은 건너뛴다.
/// </summary>
internal static class MovePage
{
    public const string Export = "export", Import = "import";

    public static string Html(string mode) => "<!doctype html><meta charset=utf-8><script>\n" + Common + (mode == Export ? ExportScript : ImportScript) + "\n</script>";

    private const string Common = """
        const req = r => new Promise((ok, no) => { r.onsuccess = () => ok(r.result); r.onerror = () => no(r.error); });
        const VIEWS = ['Int8Array', 'Uint8Array', 'Uint8ClampedArray', 'Int16Array', 'Uint16Array', 'Int32Array', 'Uint32Array',
          'Float32Array', 'Float64Array', 'BigInt64Array', 'BigUint64Array', 'DataView'];
        const fail = e => fetch('/__desk/fail', { method: 'POST', body: String(e && e.stack || e) });
        """;

    private const string ExportScript = """
        (async () => {
          const CH = 8 * 1024 * 1024;
          let n = 0;
          const post = async (name, part, body) => {
            const r = await fetch(`/__desk/put?name=${encodeURIComponent(name)}&part=${part}`, { method: 'POST', body });
            if (!r.ok) throw new Error('put ' + name + ' ' + r.status);
          };
          const put = async (name, blob) => {
            if (blob.size === 0) return post(name, 0, blob);
            for (let off = 0, part = 0; off < blob.size; off += CH, part++) await post(name, part, blob.slice(off, off + CH));
          };
          const json = o => new Blob([JSON.stringify(o)], { type: 'application/json' });
          const b64 = buf => { let s = ''; for (const c of new Uint8Array(buf)) s += String.fromCharCode(c); return btoa(s); };
          const viewBytes = v => v.buffer.slice(v.byteOffset, v.byteOffset + v.byteLength);
          // blobs가 null이면(키) 바이너리를 글자로 바로 담는다. 아니면 따로 보낼 조각으로 뺀다.
          const enc = (v, blobs, seen) => {
            if (v === null || typeof v === 'boolean' || typeof v === 'string') return v;
            if (typeof v === 'number') return Number.isFinite(v) && !Object.is(v, -0) ? v : { $: 'num', v: Object.is(v, -0) ? '-0' : String(v) };
            if (typeof v === 'undefined') return { $: 'undef' };
            if (typeof v === 'bigint') return { $: 'big', v: v.toString() };
            if (typeof v !== 'object') throw new Error('skip');
            if (seen.has(v)) throw new Error('skip'); // 순환 참조
            seen.add(v);
            const ref = b => { const name = `blob/${n++}`; blobs.push({ name, b }); return name; };
            try {
              if (Array.isArray(v)) return v.map(x => enc(x, blobs, seen));
              if (v instanceof Date) return { $: 'date', v: String(v.getTime()) };
              if (v instanceof RegExp) return { $: 're', s: v.source, f: v.flags };
              if (v instanceof Map) return { $: 'map', v: [...v].map(([k, x]) => [enc(k, blobs, seen), enc(x, blobs, seen)]) };
              if (v instanceof Set) return { $: 'set', v: [...v].map(x => enc(x, blobs, seen)) };
              if (v instanceof ArrayBuffer) return blobs ? { $: 'ab', b: ref(new Blob([v])) } : { $: 'ab64', v: b64(v) };
              if (ArrayBuffer.isView(v)) {
                const t = v.constructor.name;
                if (!VIEWS.includes(t)) throw new Error('skip');
                return blobs ? { $: 'view', t, b: ref(new Blob([viewBytes(v)])) } : { $: 'view64', t, v: b64(viewBytes(v)) };
              }
              if (v instanceof Blob) {
                if (!blobs) throw new Error('skip');
                return v instanceof File ? { $: 'file', b: ref(v), t: v.type, n: v.name, m: v.lastModified } : { $: 'blob', b: ref(v), t: v.type };
              }
              if (v instanceof Boolean || v instanceof Number || v instanceof String) return { $: 'boxed', v: enc(v.valueOf(), blobs, seen) };
              if (v instanceof Error) return { $: 'err', n: v.name, m: v.message };
              const proto = Object.getPrototypeOf(v);
              if (proto !== Object.prototype && proto !== null) throw new Error('skip');
              const o = Object.create(null);
              for (const k of Object.keys(v)) o[k] = enc(v[k], blobs, seen);
              return { $: 'obj', v: o };
            } finally { seen.delete(v); }
          };
          const index = { v: 1, local: null, idb: [], opfs: [], skipped: 0 };

          const ls = Object.create(null);
          for (let i = 0; i < localStorage.length; i++) { const k = localStorage.key(i); ls[k] = localStorage.getItem(k); }
          await put('local.json', json(ls));
          index.local = 'local.json';

          for (const info of await indexedDB.databases()) {
            const db = await req(indexedDB.open(info.name));
            const d = { name: db.name, version: db.version, stores: [] };
            for (const sn of db.objectStoreNames) {
              const st0 = db.transaction(sn).objectStore(sn);
              const s = { name: sn, keyPath: st0.keyPath, autoIncrement: st0.autoIncrement, chunks: [],
                indexes: [...st0.indexNames].map(i => { const x = st0.index(i); return { name: x.name, keyPath: x.keyPath, unique: x.unique, multiEntry: x.multiEntry }; }) };
              if (s.autoIncrement) {
                const r = await fetch(`/__desk/metadata?db=${encodeURIComponent(db.name)}&store=${encodeURIComponent(sn)}`);
                if (!r.ok) throw new Error('키 생성기 정보를 읽지 못했어요');
                s.next = (await r.json()).keyGeneratorValue;
              }
              let last, has = false;
              while (true) {
                const st = db.transaction(sn).objectStore(sn);
                const range = has ? IDBKeyRange.lowerBound(last, true) : null;
                const [keys, vals] = await Promise.all([req(st.getAllKeys(range, 200)), req(st.getAll(range, 200))]);
                if (!keys.length) break;
                const recs = [], blobs = [];
                keys.forEach((k, i) => {
                  const mine = [];
                  try { recs.push([enc(k, null, new Set()), enc(vals[i], mine, new Set())]); blobs.push(...mine); }
                  catch { index.skipped++; }
                });
                const name = `idb/${n++}.json`;
                await put(name, json(recs));
                for (const x of blobs) await put(x.name, x.b);
                s.chunks.push(name);
                last = keys[keys.length - 1];
                has = true;
                if (keys.length < 200) break;
              }
              d.stores.push(s);
            }
            db.close();
            index.idb.push(d);
          }

          if (navigator.storage && navigator.storage.getDirectory) {
            const walk = async (dir, prefix) => {
              for await (const [name, h] of dir.entries()) {
                const path = prefix + name;
                if (h.kind === 'directory') { index.opfs.push({ path, dir: true }); await walk(h, path + '/'); }
                else { const file = `opfs/${n++}`; await put(file, await h.getFile()); index.opfs.push({ path, file }); }
              }
            };
            await walk(await navigator.storage.getDirectory(), '');
          }

          await put('index.json', json(index));
          await fetch('/__desk/done?skipped=' + index.skipped, { method: 'POST' });
        })().catch(fail);
        """;

    private const string ImportScript = """
        (async () => {
          const get = async name => {
            const r = await fetch('/__desk/get?name=' + encodeURIComponent(name));
            if (!r.ok) throw new Error('get ' + name + ' ' + r.status);
            return r;
          };
          const fromB64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0)).buffer;
          const view = (t, ab) => { if (!VIEWS.includes(t)) throw new Error('모르는 값'); return t === 'DataView' ? new DataView(ab) : new globalThis[t](ab); };
          const dec = async v => {
            if (v === null || typeof v !== 'object') return v;
            if (Array.isArray(v)) { const a = []; for (const x of v) a.push(await dec(x)); return a; }
            switch (v.$) {
              case 'num': return v.v === '-0' ? -0 : Number(v.v);
              case 'undef': return undefined;
              case 'big': return BigInt(v.v);
              case 'date': return new Date(Number(v.v));
              case 're': return new RegExp(v.s, v.f);
              case 'map': { const m = new Map(); for (const [k, x] of v.v) m.set(await dec(k), await dec(x)); return m; }
              case 'set': { const s = new Set(); for (const x of v.v) s.add(await dec(x)); return s; }
              case 'ab': return (await get(v.b)).arrayBuffer();
              case 'view': return view(v.t, await (await get(v.b)).arrayBuffer());
              case 'ab64': return fromB64(v.v);
              case 'view64': return view(v.t, fromB64(v.v));
              case 'blob': return new Blob([await (await get(v.b)).blob()], { type: v.t });
              case 'file': return new File([await (await get(v.b)).blob()], v.n, { type: v.t, lastModified: v.m });
              case 'boxed': return Object(await dec(v.v));
              case 'err': { const e = new Error(v.m); e.name = v.n; return e; }
              case 'obj': { const o = {}; for (const [k, x] of Object.entries(v.v)) Object.defineProperty(o, k, { value: await dec(x), enumerable: true, writable: true, configurable: true }); return o; }
            }
            throw new Error('모르는 값');
          };
          const index = await (await get('index.json')).json();

          if (index.local) for (const [k, v] of Object.entries(await (await get(index.local)).json())) localStorage.setItem(k, v);

          for (const d of index.idb) {
            const open = indexedDB.open(d.name, d.version);
            open.onupgradeneeded = () => {
              for (const s of d.stores) {
                const st = open.result.createObjectStore(s.name, { keyPath: s.keyPath, autoIncrement: s.autoIncrement });
                for (const i of s.indexes) st.createIndex(i.name, i.keyPath, { unique: i.unique, multiEntry: i.multiEntry });
              }
            };
            const db = await req(open);
            for (const s of d.stores) {
              // 삭제된 높은 키도 재사용하지 않도록 생성기를 먼저 복원한다. 임시 레코드는 같은 트랜잭션에서 지운다.
              if (s.autoIncrement && s.next > 1) {
                const key = s.next - 1, value = {};
                if (s.keyPath !== null) {
                  const path = s.keyPath.split('.'); let at = value;
                  for (const p of path.slice(0, -1)) {
                    const child = {};
                    Object.defineProperty(at, p, { value: child, enumerable: true, writable: true, configurable: true }); at = child;
                  }
                  Object.defineProperty(at, path[path.length - 1], { value: key, enumerable: true, writable: true, configurable: true });
                }
                const tx = db.transaction(s.name, 'readwrite'), st = tx.objectStore(s.name);
                if (s.keyPath === null) st.put(value, key); else st.put(value);
                st.delete(key);
                await new Promise((ok, no) => { tx.oncomplete = ok; tx.onerror = () => no(tx.error); tx.onabort = () => no(tx.error); });
              }
              for (const chunk of s.chunks) {
                const rows = [];
                for (const [k, v] of await (await get(chunk)).json()) rows.push([await dec(k), await dec(v)]);
                const tx = db.transaction(s.name, 'readwrite'), st = tx.objectStore(s.name);
                for (const [k, v] of rows) { if (s.keyPath === null) st.put(v, k); else st.put(v); }
                await new Promise((ok, no) => { tx.oncomplete = ok; tx.onerror = () => no(tx.error); tx.onabort = () => no(tx.error); });
              }
            }
            db.close();
          }

          if (index.opfs.length) {
            const root = await navigator.storage.getDirectory();
            for (const e of index.opfs) {
              const parts = e.path.split('/'), name = parts.pop();
              let dir = root;
              for (const p of parts) dir = await dir.getDirectoryHandle(p, { create: true });
              if (e.dir) { await dir.getDirectoryHandle(name, { create: true }); continue; }
              const w = await (await dir.getFileHandle(name, { create: true })).createWritable();
              const r = await get(e.file);
              if (r.body) await r.body.pipeTo(w); else await w.close();
            }
          }

          await fetch('/__desk/done', { method: 'POST' });
        })().catch(fail);
        """;
}
