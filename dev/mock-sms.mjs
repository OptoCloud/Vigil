// A stand-in for an SMS gateway (ADR-027), for watching vigil-core's texts without
// the modem. Speaks the contract vigil defines: POST, `Authorization: Bearer <token>`, a JSON
// body `{ "message": "…" }`, 202 for accepted. Prints each text it would have sent.
//
//   node dev/mock-sms.mjs [port] [token]    then run vigil-core with
//                                           SMS_URL=http://127.0.0.1:5179/send SMS_TOKEN=dev-token
//
// Type a letter + Enter: f = refuse every send (503), a = wrong token from now on (as if the
// gateway's had changed), c = back to normal.

import http from 'node:http';

const port = Number(process.argv[2] ?? 5179);
const token = process.argv[3] ?? 'dev-token';
/** More than any text vigil sends; a body past it is refused unread, as the real gateway should. */
const MAX_BODY_BYTES = 16 * 1024;

const state = { refusing: false, rotated: false };

http
	.createServer((request, response) => {
		const reply = (status, why) => {
			if (why) console.log(`${new Date().toISOString()}  ${status}: ${why}`);
			response.writeHead(status).end();
		};

		if (request.method !== 'POST' || request.url !== '/send') return reply(404, `${request.method} ${request.url}`);
		const expected = `Bearer ${state.rotated ? 'not-the-one-you-have' : token}`;
		if (request.headers.authorization !== expected) return reply(401, 'wrong or missing bearer token');

		let body = '';
		request.on('data', (chunk) => {
			body += chunk;
			if (body.length > MAX_BODY_BYTES) request.destroy();
		});
		request.on('end', () => {
			if (state.refusing) return reply(503, 'refusing, as asked (f)');
			try {
				const { message } = JSON.parse(body);
				if (typeof message !== 'string') return reply(400, `no "message" in ${body}`);
				console.log(`${new Date().toISOString()}  text (${message.length} chars): ${message}`);
				reply(202);
			} catch {
				reply(400, `not JSON: ${body}`);
			}
		});
	})
	.listen(port, '127.0.0.1', () => console.log(`mock SMS gateway on http://127.0.0.1:${port}/send, token "${token}" (f/a/c + Enter)`));

process.stdin.on('data', (input) => {
	const key = input.toString().trim();
	if (key === 'f') state.refusing = true;
	else if (key === 'a') state.rotated = true;
	else if (key === 'c') Object.assign(state, { refusing: false, rotated: false });
	console.log('state:', state);
});
