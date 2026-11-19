// A stand-in for vigild, for developing vigil-core and the dashboard without the hardware
// (and on Windows, where the real daemon's unix socket isn't an option). Serves the same
// GET /status and GET /events over TCP, with plausibly wandering temperatures.
//
//   node dev/mock-vigild.mjs [port]            then run vigil-core with VIGILD_URL=http://127.0.0.1:5178
//
// Type a letter + Enter to inject a fault: s = stall a fan, g = GPU unreadable,
// u = loop unhealthy, r = grow a drive's reallocated count, z = fault a pool disk onto a spare
// (tank's night of 2026-10-08), o = take a pool disk offline (DEGRADED, no resilver, no
// errors), b = reboot the host (every /proc counter starts again from zero), c = clear all faults.

import http from 'node:http';

const port = Number(process.argv[2] ?? 5178);
const POLL_MS = 2000;

const faults = { stall: false, gpu: false, unhealthy: false, pool: false, offline: false };

/** vigild polls zpool every 60 s and repeats that poll in every snapshot until the next. */
const ZFS_POLL_MS = 60_000;
let reallocated = 1048;
let driveHealthAsOf = new Date().toISOString();

// the host's ports: eight on the LSI's phys, three in the 4-bay stack (ata-3 the SMR scratch disk,
// ata-4 and ata-6 the hot spares; ata-5 is empty), and the two SSDs on ata-1 and ata-2.
const PORTS = [
	...Array.from({ length: 8 }, (_, phy) => `pci-0000:03:00.0-sas-phy${phy}-lun-0`),
	'pci-0000:01:00.1-ata-3',
	'pci-0000:01:00.1-ata-4',
	'pci-0000:01:00.1-ata-6',
	'pci-0000:01:00.1-ata-1',
	'pci-0000:01:00.1-ata-2'
];

const drives = Array.from({ length: 13 }, (_, i) => ({
	wwn: `naa.5000c500${(0xa0000000 + i * 0x1111111).toString(16)}`,
	device: `sd${String.fromCharCode(97 + i)}`,
	port: PORTS[i],
	base: 30 + ((i * 7) % 9),
	ssd: i >= 11
}));

// deploy/vigild.toml's [[bays]]: the 8-bay is phy0-7 top to bottom, the 4-bay ata-3, 4, 6, 5.
const BAYS = [
	...Array.from({ length: 8 }, (_, phy) => ({ port: `pci-0000:03:00.0-sas-phy${phy}-lun-0`, cage: '8-bay', slot: phy + 1 })),
	...[3, 4, 6, 5].map((ata, i) => ({ port: `pci-0000:01:00.1-ata-${ata}`, cage: '4-bay', slot: i + 1 }))
];

const fans = [
	{ id: 'drive-cage', rpmPerPercent: 18.6 },
	{ id: 'cpu-cooler-front', rpmPerPercent: 17.2 },
	{ id: 'cpu-cooler-rear', rpmPerPercent: 17.6 },
	{ id: 'intake-cpu', rpmPerPercent: 16.4 },
	{ id: 'intake-gpu-lsi', rpmPerPercent: 12.7 },
	{ id: 'lsi-cooling', rpmPerPercent: 56 }
];

// A slow load cycle plus noise, so the charts have some shape to them.
const wave = (periodSeconds, phase = 0) => Math.sin((Date.now() / 1000 / periodSeconds) * 2 * Math.PI + phase);
const noise = (amount) => (Math.random() - 0.5) * amount;
const duty = (celsius, low, high) => Math.round(Math.min(100, Math.max(30, 30 + ((celsius - low) / (high - low)) * 70)));

const TIB = 2 ** 40;

// void: a raidz2 of the eight LSI disks with ata-4 and ata-6 as hot spares; rpool: the SSD mirror.
// Under the pool fault, the sixth disk is FAULTED and the first spare stands in for it.
let zfsPolledAt = Date.now();
setInterval(() => (zfsPolledAt = Date.now()), ZFS_POLL_MS);

function zfs() {
	const leaf = (d, parent, changes = {}) => ({
		name: `ata-ST4000VN008_${d.wwn.slice(-6).toUpperCase()}`,
		guid: String(9000000000000000000n + BigInt(d.wwn.length * 1000 + drives.indexOf(d))),
		path: `/dev/disk/by-id/ata-ST4000VN008_${d.wwn.slice(-6).toUpperCase()}-part1`,
		deviceName: d.wwn,
		port: d.port,
		parent,
		class: 'normal',
		state: 'ONLINE',
		aux: null,
		readErrors: 0,
		writeErrors: 0,
		checksumErrors: 0,
		slowIos: 0,
		...changes
	});
	const idle = { parent: null, class: 'spare', state: 'AVAIL', readErrors: null, writeErrors: null, checksumErrors: null, slowIos: null };
	const members = drives.slice(0, 8).map((d, i) =>
		faults.pool && i === 5
			? leaf(d, 'spare-5', { state: 'FAULTED', aux: 'ERR_EXCEEDED', readErrors: 14 })
			: faults.offline && i === 2
				? leaf(d, 'raidz2-0', { state: 'OFFLINE' })
				: leaf(d, 'raidz2-0')
	);
	const spares = faults.pool
		? [leaf(drives[9], 'spare-5', { class: 'spare', state: 'INUSE' }), leaf(drives[10], null, idle)]
		: [leaf(drives[9], null, idle), leaf(drives[10], null, idle)];
	const started = Math.floor(Date.now() / 1000) - 3 * 3600;
	return {
		isAvailable: true,
		// A fault shows at once rather than at the next whole minute, so the page can be watched reacting.
		asOf: new Date(zfsPolledAt).toISOString(),
		pools: [
			{
				name: 'tank',
				state: faults.pool || faults.offline ? 'DEGRADED' : 'ONLINE',
				errorCount: 0,
				sizeBytes: Math.round(29.1 * TIB),
				allocatedBytes: Math.round(11.8 * TIB),
				scan: faults.pool
					? { function: 'RESILVER', state: 'SCANNING', startTime: new Date(started * 1000).toISOString(), endTime: null, toExamineBytes: Math.round(11.8 * TIB), examinedBytes: Math.round(7.1 * TIB), issuedBytes: Math.round(4.2 * TIB), processedBytes: Math.round(0.5 * TIB), errors: 0 }
					: { function: 'SCRUB', state: 'FINISHED', startTime: '2026-10-11T00:24:01.000Z', endTime: '2026-10-11T09:10:54.000Z', toExamineBytes: Math.round(11.8 * TIB), examinedBytes: Math.round(11.8 * TIB), issuedBytes: Math.round(11.8 * TIB), processedBytes: 0, errors: 0 },
				vdevs: [...members, ...spares]
			},
			{
				name: 'rpool',
				state: 'ONLINE',
				errorCount: 0,
				sizeBytes: Math.round(0.45 * TIB),
				allocatedBytes: Math.round(0.12 * TIB),
				scan: null,
				vdevs: drives.slice(11).map((d) => leaf(d, 'mirror-0'))
			}
		]
	};
}

// The host's /proc counters (ADR-025): they only grow, at rates that follow the same slow
// waves, so vigil-core has something to turn into rates. A nightly-backup burst every 20 min.
const GIB = 2 ** 30;
const HOST_CPUS = 16;
const TICKS_PER_SECOND = 100;
let host = freshHost();
function freshHost() {
	return {
		bootedAt: Date.now(),
		readAt: Date.now(),
		cpu: { user: 0, nice: 0, system: 0, idle: 0, iowait: 0, irq: 0, softirq: 0, steal: 0 },
		pressure: { cpu: [0, 0], memory: [0, 0], io: [0, 0] },
		disks: Object.fromEntries(drives.map((d) => [d.wwn, { read: 0, written: 0, busy: 0 }])),
		net: { vmbr0: [0, 0] }
	};
}

function hostStatus() {
	const now = Date.now();
	const seconds = (now - host.readAt) / 1000;
	host.readAt = now;

	const backup = wave(1200) > 0.85;
	const busy = Math.min(0.95, Math.max(0.03, 0.18 + wave(600) * 0.12 + noise(0.04) + (backup ? 0.3 : 0)));
	const iowait = backup ? 0.08 : 0.01;
	const ticks = seconds * TICKS_PER_SECOND * HOST_CPUS;
	host.cpu.user += Math.round(ticks * busy * 0.7);
	host.cpu.system += Math.round(ticks * busy * 0.25);
	host.cpu.softirq += Math.round(ticks * busy * 0.05);
	host.cpu.iowait += Math.round(ticks * iowait);
	host.cpu.idle += Math.round(ticks * (1 - busy - iowait));

	const micros = seconds * 1e6;
	host.pressure.cpu[0] += Math.round(micros * busy * 0.05);
	host.pressure.memory[0] += Math.round(micros * 0.002);
	host.pressure.memory[1] += Math.round(micros * 0.001);
	host.pressure.io[0] += Math.round(micros * (backup ? 0.2 : 0.02));
	host.pressure.io[1] += Math.round(micros * (backup ? 0.12 : 0.01));

	const disks = drives.map((d, i) => {
		const counters = host.disks[d.wwn];
		const pooled = i < 8 || d.ssd;
		const readRate = pooled ? (backup ? 140e6 : 4e6 + Math.max(0, wave(300, i)) * 20e6) : 0;
		const writeRate = pooled ? (backup ? 30e6 : 1e6 + Math.max(0, wave(450, i)) * 6e6) : 0;
		counters.read += Math.round(readRate * seconds);
		counters.written += Math.round(writeRate * seconds);
		counters.busy += Math.round(seconds * 1000 * Math.min(1, (readRate + writeRate) / 180e6));
		return { deviceName: d.wwn, port: d.port, readBytes: counters.read, writtenBytes: counters.written, busyMillis: counters.busy };
	});

	host.net.vmbr0[0] += Math.round(seconds * (2e6 + Math.max(0, wave(900)) * 40e6));
	host.net.vmbr0[1] += Math.round(seconds * (0.5e6 + (backup ? 60e6 : 0)));

	const total = 64 * GIB;
	const arc = 31 * GIB + wave(3600) * GIB;
	const stall = ([some, full]) => ({ someMicros: some, fullMicros: full });
	return {
		asOf: new Date(now).toISOString(),
		uptimeSeconds: Math.round((now - host.bootedAt) / 10) / 100,
		memory: {
			totalBytes: total,
			availableBytes: Math.round(total - arc - 14 * GIB - wave(800) * 2 * GIB),
			freeBytes: Math.round(3 * GIB),
			buffersBytes: Math.round(0.2 * GIB),
			cachedBytes: Math.round(6 * GIB),
			swapTotalBytes: 8 * GIB,
			swapFreeBytes: 8 * GIB - 40 * 2 ** 20,
			arcBytes: Math.round(arc),
			arcMaxBytes: 32 * GIB
		},
		cpu: { ...host.cpu, logicalCpus: HOST_CPUS },
		load: {
			one: Math.round(busy * HOST_CPUS * 100) / 100,
			five: Math.round((0.18 + wave(600, -0.3) * 0.1) * HOST_CPUS * 100) / 100,
			fifteen: Math.round((0.18 + wave(600, -1) * 0.05) * HOST_CPUS * 100) / 100
		},
		pressure: { cpu: { someMicros: host.pressure.cpu[0], fullMicros: 0 }, memory: stall(host.pressure.memory), io: stall(host.pressure.io) },
		disks,
		interfaces: [{ name: 'vmbr0', receivedBytes: host.net.vmbr0[0], transmittedBytes: host.net.vmbr0[1] }]
	};
}

function snapshot() {
	const cpu = 46 + wave(600) * 12 + noise(1.5);
	const gpu = 40 + wave(900, 1) * 6 + noise(1);
	const hba = 54 + wave(1200, 2) * 3 + noise(0.4);
	const driveTemps = drives.map((d) => Math.round(d.base + wave(1800, d.base) * 2 + noise(0.6)));
	const hottestDrive = Math.max(...driveTemps);

	const reading = (id, category, label, celsius) => ({
		id,
		category,
		label,
		celsiusOrNull: celsius === null ? null : Math.round(celsius * 100) / 100,
		sourcePath: 'mock',
		isAvailable: celsius !== null
	});

	const duties = {
		'drive-cage': duty(hottestDrive, 28, 48),
		'cpu-cooler-front': duty(cpu, 30, 75),
		'cpu-cooler-rear': duty(cpu, 30, 75),
		'intake-cpu': duty(cpu, 30, 80),
		'intake-gpu-lsi': faults.gpu ? 80 : duty(Math.max(gpu, hba), 40, 85),
		'lsi-cooling': duty(Math.max(hba, hottestDrive), 45, 75)
	};

	return {
		timestampUtc: new Date().toISOString(),
		sensors: [
			reading('cpu', 'cpu', 'Tctl', cpu),
			reading('board', 'boardAmbient', 'SYSTIN', 33 + wave(2400) + noise(0.3)),
			...drives.map((d, i) => ({ ...reading(`drive:${d.wwn}`, 'drive', d.wwn, driveTemps[i]), port: d.port })),
			reading('dimm:hwmon15', 'memory', 'hwmon15', 32 + wave(1500) * 1.5),
			reading('dimm:hwmon16', 'memory', 'hwmon16', 33 + wave(1500, 1) * 1.5),
			reading('gpu', 'gpu', 'GPU', faults.gpu ? null : gpu),
			reading('hba', 'hba', 'IOC Temperature', hba)
		],
		fans: fans.map((fan) => {
			const stalled = faults.stall && fan.id === 'drive-cage';
			return {
				id: fan.id,
				dutyPercent: duties[fan.id],
				rpm: stalled ? 0 : Math.round(duties[fan.id] * fan.rpmPerPercent + noise(20)),
				mode: 'manual',
				stalled
			};
		}),
		driveHealth: drives.map((d, i) => ({
			deviceName: d.wwn,
			port: d.port,
			passed: true,
			reallocatedSectorCount: i === 2 ? reallocated : 0,
			pendingSectorCount: d.ssd ? null : 0,
			powerOnHours: 9000 + i * 1300,
			serialNumber: `ZDH8${d.wwn.slice(-4).toUpperCase()}`,
			// SSDs answer neither the ATA counters nor a self-test log here, as the host's NVMe does not.
			reportedUncorrect: d.ssd ? null : 0,
			commandTimeout: d.ssd ? null : 0,
			// The 4-bay's flaky cable (the 10-16 ata2 reset): a few CRC errors that the r key grows too.
			udmaCrcErrorCount: d.ssd ? null : i === 9 ? 3 + (reallocated - 1048) / 8 : 0,
			lastSelfTestPassed: d.ssd ? null : true,
			lastSelfTestHours: d.ssd ? null : 9000 + i * 1300 - 30,
			sourcePath: `/dev/${d.device}`,
			isAvailable: true,
			asOf: driveHealthAsOf
		})),
		zfs: zfs(),
		bays: BAYS,
		host: hostStatus(),
		controlLoopHealthy: !faults.unhealthy
	};
}

const clients = new Set();
setInterval(() => {
	const event = `event: status\ndata: ${JSON.stringify(snapshot())}\n\n`;
	for (const client of clients) client.write(event);
}, POLL_MS);

http
	.createServer((request, response) => {
		if (request.url === '/status') {
			response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(snapshot()));
		} else if (request.url === '/events') {
			response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
			response.write(`event: status\ndata: ${JSON.stringify(snapshot())}\n\n`);
			clients.add(response);
			request.on('close', () => clients.delete(response));
		} else {
			response.writeHead(404).end();
		}
	})
	.listen(port, '127.0.0.1', () => console.log(`mock vigild on http://127.0.0.1:${port} (s/g/u/r/z/o/b/c + Enter to inject faults)`));

process.stdin.on('data', (input) => {
	const key = input.toString().trim();
	if (key === 's') faults.stall = true;
	else if (key === 'g') faults.gpu = true;
	else if (key === 'u') faults.unhealthy = true;
	else if (key === 'r') {
		reallocated += 8;
		driveHealthAsOf = new Date().toISOString();
	} else if (key === 'z') faults.pool = true;
	else if (key === 'o') faults.offline = true;
	else if (key === 'b') host = freshHost();
	else if (key === 'c') faults.stall = faults.gpu = faults.unhealthy = faults.pool = faults.offline = false;
	zfsPolledAt = Date.now();
	console.log('faults:', faults, 'reallocated:', reallocated);
});
