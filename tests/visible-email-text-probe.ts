import { visibleEmailText } from "../../../artifacts/api-server/src/lib/classifier-features.ts";

const payload = Buffer.from(process.argv[2] ?? "", "base64").toString("utf8");
const expected = JSON.parse(Buffer.from(process.argv[3] ?? "", "base64").toString("utf8")) as string[];
const visible = visibleEmailText(payload);
for (const token of expected) {
  if (!visible.includes(token)) {
    throw new Error(`actual visibleEmailText swallowed required token: ${token}`);
  }
}
process.stdout.write(visible);