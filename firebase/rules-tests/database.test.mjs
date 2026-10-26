import { before, beforeEach, after, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { initializeTestEnvironment, assertFails, assertSucceeds } from '@firebase/rules-unit-testing';
import { get, ref, set } from 'firebase/database';

let env;
const sessions = { alice: 'a'.repeat(32), bob: 'b'.repeat(32) };
const signal = () => ({ FromSession: sessions.alice, ToSession: sessions.bob, Type: 'offer', Sdp: 'test-sdp' });
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

test('baseline P0-1: an authenticated outsider CAN read a private voice attachment in profiles', async () => {
  const data = await assertSucceeds(get(ref(db('outsider'), 'profiles/alice/Files/file/Chunks/c00')));
  assert.equal(data.val(), 'AQID');
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
