# Security

Keep the API token in server-side secret configuration. Never put it in a
browser, mobile application, message header, log, or exception.

The adapter is off by default and requires configuration consent plus per-call
paid consent. Critical security/account mail requires an additional per-call
consent. It refuses redirects, makes one attempt, enforces a 60-second maximum
configured deadline and 1 MiB encoded request/response limits, and does not
include recipients or attachments in classification.

Displayed plain-text and HTML MIME parts are decoded by the installed MimeKit
package. HTML tags are removed while hidden/script text is deliberately
over-included; every resulting fragment is made structurally inert before all
fragments are joined. Unsupported
displayed MIME, nested messages, signed bodies, and encrypted bodies are rejected
before a paid request. Failure policy remains an explicit host decision.

Framing adds a non-base64 prefix/suffix and late-decoded entities for QP/base64
punctuation, header delimiters, markup, and CSS braces. Regression tests pass the
exact JSON body captured from the adapter into the checked-in production
`visibleEmailText` function, covering CSS-brace removal, quoted-printable,
base64 MIME headers, and whole-body base64 autodetection.

Response parsing validates the optional `flaggedTermCount` as a non-negative
integer and recursively validates the complete optional content-audit object:
exact fields, integer bounds, enums, nested counts, issue and good-practice
items, array limits, homoglyph term limits, and the truncation boolean.

Report vulnerabilities privately to support@sendrepute.com without credentials
or customer message content.