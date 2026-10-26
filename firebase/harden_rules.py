"""Generate bounded RTDB schemas while preserving the original seat/SDP authorization expressions."""
import json
from pathlib import Path

ROOT = Path(__file__).parent
path = ROOT / "database.rules.json"
rules = json.loads(path.read_text())["rules"]
owner = "auth != null && auth.uid === $uid"
member = "auth != null && root.child('communities').child($community).child('members').child(auth.uid).val() === true"
sha = "/^[a-f0-9]{64}$/"
hex_session = "/^[a-f0-9]{32}$/"


def string(maximum, minimum=0):
    return {".validate": f"newData.isString() && newData.val().length >= {minimum} && newData.val().length <= {maximum}"}


def record(fields, required=()):
    result = dict(fields)
    result[".validate"] = "newData.hasChildren([" + ",".join(repr(key) for key in required) + "])" if required else "newData.hasChildren()"
    result["$other"] = {".validate": False}
    return result


def profile(expected_id=None):
    fields = {
        "Id": {".validate": "newData.isString() && newData.val().length <= 128" + (f" && newData.val() === {expected_id}" if expected_id else "")},
        "Username": string(32, 3), "About": string(2000),
        "Status": {".validate": "newData.val() === 'online' || newData.val() === 'idle' || newData.val() === 'dnd' || newData.val() === 'offline'"},
        "Game": string(200), "AvatarPath": {".validate": False}, "BannerPath": {".validate": False},
        "AvatarAssetId": string(128), "BannerAssetId": string(128),
        "AvatarRevision": {".validate": f"newData.isString() && newData.val().matches({sha})"}
    }
    return record(fields, ("Id", "Username"))


def image(kind, maximum, expected_id):
    return record({
        "Id": {".validate": f"newData.isString() && newData.val().matches({sha})" + (f" && newData.val() === {expected_id}" if expected_id else "")},
        "Name": string(200, 1), "Kind": {".validate": f"newData.val() === '{kind}'"},
        "Data": {".validate": f"newData.isString() && newData.val().length > 0 && newData.val().length <= {maximum} && newData.val().matches(/^[A-Za-z0-9+\\/]*={{0,2}}$/)"}
    }, ("Id", "Name", "Kind", "Data"))


info = profile("$uid")
info.update({".read": "auth != null", ".write": owner})
avatar = image("avatar", 5592408, None)
avatar.update({".read": "auth != null", ".write": owner})
profile_node = {
    ".read": owner,
    ".validate": "newData.hasChildren(['Id','Username'])",
    "Id": {".read": "auth != null", ".write": owner, ".validate": "newData.val() === $uid"},
    "Username": {**string(32, 3), ".read": "auth != null", ".write": owner},
    "Info": info,
    "Files": {".read": owner, ".write": False},
    "Images": {
        "avatar": avatar,
        "$legacy": {
            ".read": f"auth != null && (auth.uid === $uid || data.child('Kind').val() === 'avatar')",
            ".write": False
        }
    },
    "$other": {".validate": False}
}
# Legacy scalar fields may be removed by their owner during a profile upgrade, never rewritten.
for key in ("About", "AvatarPath", "BannerPath", "Status", "Game", "AvatarAssetId", "BannerAssetId", "AvatarRevision"):
    profile_node[key] = {".write": owner + " && !newData.exists()", ".validate": False}
rules["profiles"]["$uid"] = profile_node

community = rules["communities"]["$community"]
chunk_write = member + " && !data.exists() && !data.parent().parent().child('Info').exists()"
chunk = {
    ".write": chunk_write,
    ".validate": "$chunk.matches(/^c(0[0-9]|[12][0-9]|3[01])$/) && newData.isString() && newData.val().length > 0 && newData.val().length <= 700000 && newData.val().length % 4 === 0 && newData.val().matches(/^[A-Za-z0-9+\\/]*={0,2}$/)"
}
manifest = record({
    "Name": string(200, 1),
    "Kind": {".validate": "newData.val() === 'attachment' || newData.val() === 'voice'"},
    "Size": {".validate": "newData.isNumber() && newData.val() >= 0 && newData.val() <= 16777216 && newData.val() % 1 === 0 && (newData.parent().child('Kind').val() !== 'voice' || newData.val() <= 5242880)"},
    "Chunks": {".validate": "newData.isNumber() && newData.val() >= 0 && newData.val() <= 32 && newData.val() % 1 === 0"}
}, ("Name", "Kind", "Size", "Chunks"))
manifest[".write"] = member + " && !data.exists()"
count = "newData.child('Chunks').val()"
size = "newData.child('Size').val()"
checks = [manifest[".validate"], f"(({size} === 0 && {count} === 0) || ({size} > 0 && ({count} - 1) * 524288 < {size} && {size} <= {count} * 524288))"]
for index in range(32):
    part = f"newData.parent().child('Chunks/c{index:02d}')"
    byte_length = f"({part}.val().length * 3 / 4 - ({part}.val().matches(/.*==$/) ? 2 : ({part}.val().matches(/.*=$/) ? 1 : 0)))"
    expected = f"({count} > {index + 1} ? 524288 : {size} - {index} * 524288)"
    checks.append(f"({count} > {index} ? ({part}.isString() && {byte_length} === {expected}) : !{part}.exists())")
manifest[".validate"] = " && ".join(checks)
community["files"] = {"$file": {
    ".validate": f"$file.matches({sha})", "Info": manifest, "Chunks": {"$chunk": chunk}, "$other": {".validate": False}
}}
emoji = image("emoji", 349528, "$emoji")
emoji.update({".write": member + " && !data.exists()"})
community["emojis"] = {"$emoji": emoji}

slots = {f"e{index:02d}": record({
    "Token": {".validate": "newData.isString() && newData.val().length >= 3 && newData.val().length <= 42 && newData.val().matches(/^:[^:]{1,40}:$/)"},
    "AssetId": {".validate": f"newData.isString() && newData.val().matches({sha})"}
}, ("Token", "AssetId")) for index in range(32)}
slots["$other"] = {".validate": False}
slots[".validate"] = "newData.hasChildren()"
message = community["messages"]["$channel"]["$message"]
message_fields = record({
    "Id": {".validate": "newData.isString() && newData.val() === $message && newData.val().length <= 128"},
    "ChannelId": {".validate": "newData.isString() && newData.val() === $channel && newData.val().length <= 128"},
    "AuthorId": {".validate": "newData.isString() && newData.val() === auth.uid"},
    "AuthorName": string(32, 3), "Text": string(8000),
    "SentAt": {".validate": "newData.isString() && newData.val().length >= 20 && newData.val().length <= 40"},
    "AttachmentId": {".validate": f"newData.isString() && newData.val().matches({sha})"}, "Emojis": slots
}, ("Id", "ChannelId", "AuthorId", "AuthorName", "Text", "SentAt"))
message_fields[".write"] = message[".write"]
community["messages"]["$channel"]["$message"] = message_fields

presence = community["presence"]["$uid"]
presence_fields = record({
    "Profile": profile("$uid"), "ChannelId": string(128, 1),
    "SeenAt": {".validate": "newData.isNumber() && newData.val() >= now - 30000 && newData.val() <= now + 10000"}
}, ("Profile", "SeenAt"))
presence_fields[".write"] = presence[".write"]
community["presence"]["$uid"] = presence_fields

snapshot = community["snapshot"]
snapshot_fields = record({
    "Community": record({"Id": {".validate": "newData.val() === $community"}, "Name": string(80, 1), "OwnerId": {".validate": "newData.val() === auth.uid"}}, ("Id", "Name", "OwnerId")),
    "Channels": {"$index": record({"Id": string(128, 1), "CommunityId": {".validate": "newData.val() === $community"}, "Name": string(64, 1), "IsVoice": {".validate": "newData.isBoolean()"}}, ("Id", "CommunityId", "Name", "IsVoice"))},
    "Members": {"$index": profile()}
}, ("Community", "Channels", "Members"))
snapshot_fields["Channels"]["$index"][".validate"] += " && $index.matches(/^(0|[1-9][0-9]?)$/)"
snapshot_fields["Members"]["$index"][".validate"] += " && $index.matches(/^(0|[1-9][0-9]{0,2})$/)"
snapshot_fields[".write"] = snapshot[".write"]
community["snapshot"] = snapshot_fields

for index in range(5):
    seat = community["rtcRooms"]["$channel"][f"s{index}"]
    seat.update({
        "UserId": string(128, 1), "SessionId": {".validate": f"newData.isString() && newData.val().matches({hex_session})"},
        "SeenAt": {".validate": "newData.isNumber()"}, "Muted": {".validate": "newData.isBoolean()"},
        "Deafened": {".validate": "newData.isBoolean()"}, "$other": {".validate": False}
    })
# Phase 3: bounded pair-private trickle candidates, attempt IDs, TTL and room-only wake markers.
room_path = "root.child('communities').child($community).child('rtcRooms').child($channel)"
def fresh_seat(user, session):
    return '(' + ' || '.join(
        f"({room_path}.child('s{i}/UserId').val() === {user} && {room_path}.child('s{i}/SessionId').val() === {session} && {room_path}.child('s{i}/SeenAt').val() >= now - 45000)"
        for i in range(5)) + ')'

session_field = {'.validate': f'newData.isString() && newData.val().matches({hex_session})'}
created_field = {'.validate': 'newData.isNumber() && newData.val() >= now - 30000 && newData.val() <= now + 10000'}
pair_member = member + ' && (auth.uid === $from || auth.uid === $to)'
def pair_auth(meta):
    return fresh_seat('$from', f"{meta}.child('FromSession').val()") + ' && ' + fresh_seat('$to', f"{meta}.child('ToSession').val()")

def attempt_matches(meta):
    # Either direction may be the deterministic offerer; answers/candidates must echo its current attempt.
    own = "root.child('rtcSignals').child($community).child($channel).child($from).child($to)"
    other = "root.child('rtcSignals').child($community).child($channel).child($to).child($from)"
    return f"(({own}.child('Type').val() === 'offer' && {own}.child('Attempt').val() === {meta}.child('Attempt').val()) || ({other}.child('Type').val() === 'offer' && {other}.child('Attempt').val() === {meta}.child('Attempt').val()))"

signal_fields = record({
    'FromSession': session_field, 'ToSession': session_field, 'Attempt': session_field,
    'Type': {'.validate': "newData.val() === 'offer' || newData.val() === 'answer'"},
    'Sdp': string(64000, 1), 'CreatedAt': created_field
}, ('FromSession', 'ToSession', 'Attempt', 'Type', 'Sdp', 'CreatedAt'))
signal_fields['.read'] = pair_member + " && (!data.exists() || (data.child('CreatedAt').val() >= now - 600000 && " + pair_auth('data') + '))'
other_offer = "root.child('rtcSignals').child($community).child($channel).child($to).child($from)"
answer_matches = f"(newData.child('Type').val() !== 'answer' || ({other_offer}.child('Type').val() === 'offer' && {other_offer}.child('Attempt').val() === newData.child('Attempt').val() && {other_offer}.child('FromSession').val() === newData.child('ToSession').val() && {other_offer}.child('ToSession').val() === newData.child('FromSession').val()))"
signal_fields['.write'] = pair_member + " && ((auth.uid === $from && newData.exists() && " + pair_auth('newData') + ' && ' + answer_matches + ") || (!newData.exists() && (auth.uid === $from || data.child('CreatedAt').val() < now - 600000)))"
rules['rtcSignals']['$community']['$channel']['$from']['$to'] = signal_fields

meta = record({'FromSession': session_field, 'ToSession': session_field, 'Attempt': session_field, 'CreatedAt': created_field}, ('FromSession', 'ToSession', 'Attempt', 'CreatedAt'))
meta['.write'] = member + " && auth.uid === $from && " + pair_auth('newData') + ' && ' + attempt_matches('newData')
candidate = record({
    'Candidate': string(2048, 1), 'SdpMid': string(64),
    'SdpMLineIndex': {'.validate': 'newData.isNumber() && newData.val() >= 0 && newData.val() <= 3 && newData.val() % 1 === 0'},
    'CreatedAt': created_field
}, ('Candidate', 'SdpMid', 'SdpMLineIndex', 'CreatedAt'))
candidate['.write'] = member + " && auth.uid === $from && $candidate.matches(/^c(0[0-9]|[1-5][0-9]|6[0-3])$/) && " + pair_auth("data.parent().child('Meta')") + ' && ' + attempt_matches("data.parent().child('Meta')")
pair = {
    '.read': pair_member + " && (!data.exists() || (data.child('Meta/CreatedAt').val() >= now - 600000 && " + pair_auth("data.child('Meta')") + '))',
    '.write': pair_member + " && !newData.exists() && (auth.uid === $from || data.child('Meta/CreatedAt').val() < now - 600000)",
    'Meta': meta, '$candidate': candidate
}
rules['rtcCandidates'] = {'$community': {'$channel': {'$from': {'.write': member + ' && auth.uid === $from && !newData.exists()', '$to': pair}}}}

# At most 5x5 small wake markers. All members can read the room; addresses/SDP stay exclusively in pair nodes.
room_node = community['rtcRooms']['$channel']
from_slot = room_path + '.child($fromSlot)'
to_slot = room_path + '.child($toSlot)'
marker = record({
    'FromSession': session_field, 'ToSession': session_field, 'Attempt': session_field,
    'Version': session_field, 'Kind': {'.validate': "newData.val() === 'signal' || newData.val() === 'candidates' || newData.val() === 'retry'"},
    'CreatedAt': created_field
}, ('FromSession', 'ToSession', 'Attempt', 'Version', 'Kind', 'CreatedAt'))
marker['.write'] = member + f" && $fromSlot.matches(/^s[0-4]$/) && $toSlot.matches(/^s[0-4]$/) && $fromSlot !== $toSlot && {from_slot}.child('UserId').val() === auth.uid && {from_slot}.child('SeenAt').val() >= now - 45000 && {to_slot}.child('SeenAt').val() >= now - 45000 && {from_slot}.child('SessionId').val() === newData.child('FromSession').val() && {to_slot}.child('SessionId').val() === newData.child('ToSession').val()"
room_node['events'] = {'$fromSlot': {
    '.write': member + f" && $fromSlot.matches(/^s[0-4]$/) && {from_slot}.child('UserId').val() === auth.uid && !newData.exists()",
    '$toSlot': marker
}}
community['$other'] = {'.validate': False}
path.write_text(json.dumps({'rules': rules}, indent=2, ensure_ascii=False) + '\n')
