/**
 * Broken Fixture — intentionally fails at load.
 *
 * Fixture for the M5 plugin management page: used to verify the
 * "最近加载错误" (recent load errors) display and A03 semantics.
 *
 * IMPORTANT (measured behavior, Pi 0.87.1):
 *   An extension passed via `-e`/`--extension` that throws at load makes Pi
 *   EXIT with code 1 — it does NOT continue with a soft error record. stderr
 *   shows:
 *     Error: Failed to load extension "<path>": ... <this message>
 *     Hint: Start without extensions using "pi -ne".
 *   For the management page this means: a broken extension is a startup
 *   failure, observable as process exit + stderr diagnostics — not a
 *   runtime warning that can be dismissed.
 */

export default function brokenFixture() {
	throw new Error("broken-fixture: intentional load failure for management-page acceptance");
}
