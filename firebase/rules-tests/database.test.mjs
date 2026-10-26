import { before, beforeEach, after, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { initializeTestEnvironment, assertFails, assertSucceeds } from '@firebase/rules-unit-testing';
import { get, ref, set, update } from 'firebase/database';
import { createMockUserToken } from '@firebase/util';

let env;
const sessions = { alice: 'a'.repeat(32), bob: 'b'.repeat(32) };
const signal = () => ({ FromSession: sessions.alice, ToSession: sessions.bob, Type: 'offer', Sdp: 'test-sdp', Attempt: 'c'.repeat(32), CreatedAt: Date.now() });
const db = uid => (uid ? env.authenticatedContext(uid) : env.unauthenticatedContext()).database();

before(async () => {
  env = await initializeTestEnvironment({
    projectId: 'demo-wisp-hardening',
    database: { host: '127.0.0.1', port: 9000, rules: await readFile(new URL('../database.rules.json', import.meta.url), 'utf8') }
  });
});
beforeEach(async () => {
  await env.clearDatabase();
  await env.withSecurityRulesDisabled(async context => {
    await set(ref(context.database()), {
      profiles: { alice: { Id: 'alice', Username: 'Alice', Files: { file: { Info: { Name: 'private.wav', Kind: 'voice', Size: 3, Chunks: 1 }, Chunks: { c00: 'AQID' } } } } },
      communities: { room: {
        members: { alice: true, bob: true, u2: true, u3: true, u4: true, u5: true },
        snapshot: { Community: { Id: 'room', OwnerId: 'alice', Name: 'Private' }, Channels: [{ Id: 'voice', CommunityId: 'room', Name: 'Voice', IsVoice: true }], Members: [{ Id: 'alice', Username: 'Alice' }] },
        rtcRooms: { voice: {
          s0: { UserId: 'alice', SessionId: sessions.alice, SeenAt: Date.now(), Muted: false, Deafened: false },
          s1: { UserId: 'bob', SessionId: sessions.bob, SeenAt: Date.now(), Muted: false, Deafened: false }
        } }
      } }
    });
  });
});
after(async () => { await env?.cleanup(); });

test('P0-1: legacy private files are readable only by their author, never through a profile parent read', async () => {
  const data = await assertSucceeds(get(ref(db('alice'), 'profiles/alice/Files/file/Chunks/c00')));
  assert.equal(data.val(), 'AQID');
  await assertFails(get(ref(db('outsider'), 'profiles/alice/Files/file/Chunks/c00')));
  await assertFails(get(ref(db('bob'), 'profiles/alice/Files/file/Info')));
  await assertFails(get(ref(db('outsider'), 'profiles/alice')));
  await assertFails(set(ref(db('alice'), 'profiles/alice/Files/new-file'), { Data: 'new' }));
});
test('anonymous profile reads and community reads by outsiders are denied', async () => {
  await assertFails(get(ref(db(null), 'profiles/alice')));
  await assertFails(get(ref(db('outsider'), 'communities/room')));
});
test('five fixed seats reject a sixth seat and duplicate user slots', async () => {
  for (let index = 2; index < 5; index++) {
    await assertSucceeds(set(ref(db(`u${index}`), `communities/room/rtcRooms/voice/s${index}`), { UserId: `u${index}`, SessionId: String(index).repeat(32), SeenAt: Date.now(), Muted: false, Deafened: false }));
  }
  await assertFails(set(ref(db('u5'), 'communities/room/rtcRooms/voice/s5'), { UserId: 'u5', SessionId: '5'.repeat(32), SeenAt: Date.now(), Muted: false, Deafened: false }));
  await assertFails(set(ref(db('alice'), 'communities/room/rtcRooms/voice/s2'), { UserId: 'alice', SessionId: sessions.alice, SeenAt: Date.now(), Muted: false, Deafened: false }));
});
test('SDP requires authenticated sender, matching fresh sessions, and authorized reader', async () => {
  await assertSucceeds(set(ref(db('alice'), 'rtcSignals/room/voice/alice/bob'), signal()));
  await assertSucceeds(get(ref(db('bob'), 'rtcSignals/room/voice/alice/bob')));
  await assertFails(get(ref(db('u2'), 'rtcSignals/room/voice/alice/bob')));
  await assertFails(set(ref(db('bob'), 'rtcSignals/room/voice/alice/bob'), signal()));
  await assertFails(set(ref(db('alice'), 'rtcSignals/room/voice/alice/bob'), { ...signal(), ToSession: 'z'.repeat(32) }));
});

const fileId = 'f'.repeat(64);
const emojiId = 'e'.repeat(64);
const manifest = { Name: 'private.wav', Kind: 'voice', Size: 3, Chunks: 1 };
const transfer = (kind, id) => ({ Id: id, Name: 'image.gif', Kind: kind, Data: 'AQID' });
const message = () => ({ Id: 'message', ChannelId: 'voice', AuthorId: 'alice', AuthorName: 'Alice', Text: 'hello', SentAt: new Date().toISOString() });

test('community files require membership; their chunks and manifest cannot be changed after publication', async () => {
  const base = `communities/room/files/${fileId}`;
  await assertFails(set(ref(db('outsider'), `${base}/Chunks/c00`), 'AQID'));
  await assertSucceeds(set(ref(db('alice'), `${base}/Chunks/c00`), 'AQID'));
  await assertFails(set(ref(db('alice'), `${base}/Chunks/c00`), 'BAUG'));
  await assertFails(set(ref(db('bob'), `${base}/Chunks/c00`), 'BAUG'));
  await assertSucceeds(set(ref(db('alice'), `${base}/Info`), manifest));
  assert.equal((await assertSucceeds(get(ref(db('bob'), `${base}/Chunks/c00`)))).val(), 'AQID');
  await assertFails(get(ref(db('outsider'), `${base}/Info`)));
  await assertFails(get(ref(db('outsider'), 'communities/room/files')));
  await assertFails(set(ref(db('alice'), `${base}/Info`), { ...manifest, Name: 'changed.wav' }));
  await assertFails(set(ref(db('bob'), `${base}/Chunks/c01`), 'AQID'));
});

test('chunks reject invalid names, non-strings, oversize values, and malformed base64', async () => {
  const base = `communities/room/files/${fileId}/Chunks`;
  await assertFails(set(ref(db('alice'), `${base}/c32`), 'AQID'));
  await assertFails(set(ref(db('alice'), `${base}/cXX`), 'AQID'));
  await assertFails(set(ref(db('alice'), `${base}/c00`), { malicious: true }));
  await assertFails(set(ref(db('alice'), `${base}/c00`), 'A'.repeat(700004)));
  await assertFails(set(ref(db('alice'), `${base}/c00`), 'not base64!'));
  await assertFails(set(ref(db('alice'), `communities/room/files/not-a-hash/Chunks/c00`), 'AQID'));
});

test('file manifest requires complete correctly-sized chunks and exactly the four bounded fields', async () => {
  const base = `communities/room/files/${fileId}`;
  await assertFails(set(ref(db('alice'), `${base}/Info`), manifest));
  await set(ref(db('alice'), `${base}/Chunks/c00`), 'AQID');
  for (const invalid of [
    { ...manifest, Extra: 'quota abuse' }, { ...manifest, Size: 16777217 },
    { ...manifest, Kind: 'sound' }, { ...manifest, Size: '3' },
    { ...manifest, Size: 4 }, { ...manifest, Chunks: 2 },
    { ...manifest, Chunks: 33 }, { ...manifest, Name: 'A'.repeat(201) }
  ]) await assertFails(set(ref(db('alice'), `${base}/Info`), invalid));
  await assertSucceeds(set(ref(db('alice'), `${base}/Info`), manifest));
});

test('avatars are public to authenticated users; emoji data is private to community members', async () => {
  await assertSucceeds(set(ref(db('alice'), 'profiles/alice/Images/avatar'), transfer('avatar', 'a'.repeat(64))));
  await assertSucceeds(get(ref(db('outsider'), 'profiles/alice/Images/avatar')));
  await assertFails(get(ref(db(null), 'profiles/alice/Images/avatar')));
  await assertFails(set(ref(db('bob'), 'profiles/alice/Images/avatar'), transfer('avatar', 'b'.repeat(64))));
  await assertFails(set(ref(db('alice'), 'profiles/alice/Images/avatar'), { ...transfer('avatar', 'a'.repeat(64)), Data: 'A'.repeat(5592412) }));
  const path = `communities/room/emojis/${emojiId}`;
  await assertSucceeds(set(ref(db('alice'), path), transfer('emoji', emojiId)));
  await assertSucceeds(get(ref(db('bob'), path)));
  await assertFails(get(ref(db('outsider'), path)));
  await assertFails(set(ref(db('bob'), path), transfer('emoji', emojiId)));
  await env.withSecurityRulesDisabled(context => set(ref(context.database(), 'profiles/alice/Images/legacy-emoji'), transfer('emoji', emojiId)));
  await assertFails(get(ref(db('outsider'), 'profiles/alice/Images/legacy-emoji')));
});

test('profile metadata bounds About and rejects unknown fields or public legacy writes', async () => {
  const path = 'profiles/alice/Info';
  const profile = { Id: 'alice', Username: 'Alice', About: 'hello', Status: 'online' };
  await assertSucceeds(set(ref(db('alice'), path), profile));
  await assertSucceeds(get(ref(db('outsider'), path)));
  await assertFails(set(ref(db('alice'), path), { ...profile, About: 'A'.repeat(2001) }));
  await assertFails(set(ref(db('alice'), path), { ...profile, Unknown: 'data' }));
  await assertFails(set(ref(db('alice'), path), { ...profile, AvatarPath: 'C:/private/picture.png' }));
  await assertFails(set(ref(db('alice'), 'profiles/alice/Unknown'), 'data'));
  await assertFails(set(ref(db('alice'), 'profiles/alice/Images/another-avatar'), transfer('avatar', 'a'.repeat(64))));
});

test('chat rejects unknown fields, invalid attachment references, long names and more than 32 emoji slots', async () => {
  const path = 'communities/room/messages/voice/message';
  for (const invalid of [
    { ...message(), Extra: 'data' }, { ...message(), AuthorName: 'A'.repeat(33) },
    { ...message(), Text: 'A'.repeat(8001) }, { ...message(), AttachmentId: '../other' },
    { ...message(), Emojis: { e32: { Token: ':wave:', AssetId: emojiId } } },
    { ...message(), Emojis: { e00: { Token: ':wave:', AssetId: emojiId, Extra: 'data' } } },
    { ...message(), Emojis: { e00: { Token: ':wave:', AssetId: 'bad' } } }
  ]) await assertFails(set(ref(db('alice'), path), invalid));
  await assertSucceeds(set(ref(db('alice'), path), { ...message(), Emojis: { e00: { Token: ':руна:', AssetId: emojiId } } }));
  await assertFails(set(ref(db('bob'), path), message()));
});

test('signaling rejects unknown fields, malformed attempt identifiers and oversized SDP', async () => {
  const path = 'rtcSignals/room/voice/alice/bob';
  for (const invalid of [
    { ...signal(), Extra: 'data' }, { ...signal(), Attempt: 'short' },
    { ...signal(), Attempt: 'z'.repeat(32) }, { ...signal(), Sdp: 'A'.repeat(64001) }
  ]) await assertFails(set(ref(db('alice'), path), invalid));
  await assertSucceeds(set(ref(db('alice'), path), { ...signal(), Attempt: 'c'.repeat(32), CreatedAt: Date.now() }));
});


test('REST null_etag creates immutable chunks once and reports retry conflicts without overwriting', async () => {
  const token = createMockUserToken({ sub: 'alice', iat: Math.floor(Date.now() / 1000) }, 'demo-wisp-hardening');
  const url = `http://127.0.0.1:9000/communities/room/files/${fileId}/Chunks/c00.json?ns=demo-wisp-hardening&auth=${encodeURIComponent(token)}`;
  const options = { method: 'PUT', headers: { 'Content-Type': 'application/json', 'if-match': 'null_etag' }, body: JSON.stringify('AQID') };
  const created = await fetch(url, options); assert.equal(created.status, 200);
  const conflict = await fetch(url, options); assert.equal(conflict.status, 412);
  assert.equal((await get(ref(db('bob'), `communities/room/files/${fileId}/Chunks/c00`))).val(), 'AQID');
});


test('profile upgrade patches metadata without deleting existing legacy files or scalar fields', async () => {
  await env.withSecurityRulesDisabled(context => set(ref(context.database(), 'profiles/alice/About'), 'legacy description'));
  await assertSucceeds(update(ref(db('alice'), 'profiles/alice'), {
    Id: 'alice', Username: 'Alice', Info: { Id: 'alice', Username: 'Alice', About: 'updated description', Status: 'online' }
  }));
  const own = (await get(ref(db('alice'), 'profiles/alice'))).val();
  assert.equal(own.About, 'legacy description');
  assert.equal(own.Files.file.Chunks.c00, 'AQID');
});

test('trickle candidates are pair-private, tied to the current attempt and bounded to 64 slots', async () => {
  await set(ref(db('alice'), 'rtcSignals/room/voice/alice/bob'), signal());
  const path = 'rtcCandidates/room/voice/alice/bob';
  const meta = { FromSession: sessions.alice, ToSession: sessions.bob, Attempt: 'c'.repeat(32), CreatedAt: Date.now() };
  const candidate = { Candidate: 'candidate:1 1 UDP 1 192.0.2.1 1234 typ host', SdpMid: '0', SdpMLineIndex: 0, CreatedAt: Date.now() };
  await assertFails(set(ref(db('alice'), `${path}/c00`), candidate));
  await assertFails(set(ref(db('alice'), `${path}/Meta`), { ...meta, Attempt: 'd'.repeat(32) }));
  await assertSucceeds(set(ref(db('alice'), `${path}/Meta`), meta));
  await assertSucceeds(set(ref(db('alice'), `${path}/c00`), candidate));
  await assertSucceeds(set(ref(db('alice'), `${path}/c63`), candidate));
  await assertSucceeds(get(ref(db('bob'), path)));
  await assertFails(get(ref(db('u2'), path)));
  await assertFails(get(ref(db('bob'), 'rtcCandidates/room/voice/alice')));
  for (const [key, data] of [['c64', candidate], ['Extra', candidate], ['c01', { ...candidate, Candidate: 'A'.repeat(2049) }], ['c01', { ...candidate, Extra: 'x' }]])
    await assertFails(set(ref(db('alice'), `${path}/${key}`), data));
  await assertFails(set(ref(db('bob'), `${path}/c01`), candidate));
});

test('answers echo the offer attempt; stale SDP becomes unreadable and only expired foreign records may be removed', async () => {
  const path = 'rtcSignals/room/voice/alice/bob';
  await set(ref(db('alice'), path), signal());
  const answer = { ...signal(), FromSession: sessions.bob, ToSession: sessions.alice, Type: 'answer' };
  await assertFails(set(ref(db('bob'), 'rtcSignals/room/voice/bob/alice'), { ...answer, Attempt: 'd'.repeat(32) }));
  await assertSucceeds(set(ref(db('bob'), 'rtcSignals/room/voice/bob/alice'), answer));
  await assertFails(set(ref(db('bob'), path), null));
  await env.withSecurityRulesDisabled(context => update(ref(context.database(), path), { CreatedAt: Date.now() - 600001 }));
  await assertFails(get(ref(db('bob'), path)));
  await assertFails(set(ref(db('u2'), path), null));
  await assertSucceeds(set(ref(db('bob'), path), null));
  for (const invalid of [{ ...signal(), CreatedAt: Date.now() - 40000 }, { ...signal(), CreatedAt: Date.now() + 20000 }, { ...signal(), Attempt: null }])
    await assertFails(set(ref(db('alice'), path), invalid));
});

test('wake markers reveal no SDP, reject spoofed sessions, sixth slots and extra fields', async () => {
  const path = 'communities/room/rtcRooms/voice/events/s0/s1';
  const marker = { FromSession: sessions.alice, ToSession: sessions.bob, Attempt: 'c'.repeat(32), Kind: 'signal', Version: 'd'.repeat(32), CreatedAt: Date.now() };
  await assertSucceeds(set(ref(db('alice'), path), marker));
  await assertSucceeds(get(ref(db('u2'), 'communities/room/rtcRooms/voice')));
  for (const invalid of [{ ...marker, Sdp: 'leak' }, { ...marker, Kind: 'unknown' }, { ...marker, FromSession: 'e'.repeat(32) }])
    await assertFails(set(ref(db('alice'), path), invalid));
  await assertFails(set(ref(db('bob'), path), marker));
  await assertFails(set(ref(db('alice'), 'communities/room/rtcRooms/voice/events/s0/s5'), marker));
  await assertSucceeds(set(ref(db('alice'), 'communities/room/rtcRooms/voice/events/s0'), null));
});
