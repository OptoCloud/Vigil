// How what vigil-core withholds reads on the page (ADR-023). vigil-core puts one marker,
// "[REDACTED]", in place of a value or in front of the part it shows: loud on the wire and in
// the database, where it must never be mistaken for data. Here it becomes a quiet mask, so a
// table of vdevs does not read as a table of errors. The marker is pinned in core/contract.json.

export const REDACTION_MARKER = '[REDACTED]';

/** What the marker draws as: dots, as a card number is masked, so it reads as hidden, not as missing. */
export const MASK = '••••';

export interface TextPart {
	text: string;
	/** True for the marker itself: a part to draw as withheld. */
	withheld: boolean;
}

/** `text` split around every marker, in order; empty parts dropped. */
export function splitWithheld(text: string): TextPart[] {
	return text
		.split(REDACTION_MARKER)
		.flatMap((piece, index) => [...(index > 0 ? [{ text: REDACTION_MARKER, withheld: true }] : []), { text: piece, withheld: false }])
		.filter((part) => part.text !== '');
}

/**
 * A by-id device name as the page shows it: `ata-ST4000VN008-2DR166_[REDACTED]0000` becomes
 * `ST4000VN008-2DR166 [REDACTED]0000`. The bus prefix says nothing the bay column does not, and
 * the underscore was udev joining model and serial. Names without a withheld serial (wwn-0x…,
 * raidz2-0, a file vdev) are left as they are; the full name stays in the row's tooltip.
 */
export function deviceLabel(name: string): string {
	const match = /^(?:ata|scsi|sas|usb|nvme)-(?<model>.+?)_(?<serial>\[REDACTED\].*)$/.exec(name);
	return match?.groups ? `${match.groups.model} ${match.groups.serial}` : name;
}
