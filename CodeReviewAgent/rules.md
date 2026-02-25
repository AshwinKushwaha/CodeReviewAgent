# Code Review Rules

## CSS Standards

### RULE-CSS-001: CSS Class Naming Convention
New CSS class names must follow the naming convention:
- Page-level classes: prefixed with `p-controller-action__description`, e.g. `p-controller-action__start-button`
- Component-level classes: prefixed with `c-my-component__description`, e.g. `c-my-component__input-row-container`
- Words separated by hyphens, all lowercase, double underscore between prefix and description.

### RULE-CSS-002: Mobile First CSS
New CSS must be written mobile first.
- Base styles target mobile viewports.
- Media queries are used only to adjust layout for larger screens, not to fix broken mobile styles.
- Avoid placing mobile-specific adjustments inside media queries when they should be in the base styles.

### RULE-CSS-003: JS Hook Class Prefix
Class names used as JavaScript hooks must be prefixed with `js-`, e.g. `js-submit-button`.
- These classes must not be used for styling purposes.

---

## C# String Handling

### RULE-STR-001: String Concatenation
String concatenation must use one of the following:
- `StringBuilder`
- `string.Format(...)`
- Interpolated strings: `$"example - {dummy}"`
- Raw string or verbatim literals where appropriate
- Direct `+` operator concatenation on multiple strings is not allowed.

### RULE-STR-002: Empty String Comparison
`string.Empty` must be used instead of `""` wherever possible.

### RULE-STR-003: String Equality Comparison
String equality must use `string.Equals(...)` or `string.Compare(...)` with an explicit `StringComparison` culture specified.
- At minimum: `StringComparison.InvariantCultureIgnoreCase`
- Direct `==` comparison on strings is not allowed.

---

## C# Code Style

### RULE-CS-001: Variable Declaration with var
`var` must be used for variable declarations when a value is assigned at the same time.
- Variable names must be meaningful and accurately describe their purpose.

### RULE-CS-002: Method Naming
Method and function names must accurately describe what they do.
- Avoid vague names such as `Process`, `Handle`, `DoStuff`, or `Execute` without further qualification.

### RULE-CS-003: Unnecessary Code Logic
Unnecessary code logic must be removed.
- Do not add null/empty checks for values that are guaranteed by the surrounding code to never be null or empty.
- Remove dead code, unreachable branches, and redundant conditions.

---

## Architecture

### RULE-ARCH-001: View Models in UI Layer
View models must reside only in the application/UI layer.
- View models must not appear in domain, infrastructure, or data layers.

### RULE-ARCH-002: Thin UI Layer
The UI layer must be kept reasonably thin.
- Business logic must be pushed towards unit-testable C# code.
- Logic must be kept out of views and JavaScript where possible.

### RULE-ARCH-003: Thin External Dependency Layer
External dependency layers (e.g. repositories, API clients) must be kept reasonably thin.
- Logic must be pushed towards unit-testable C# classes, not embedded in dependency adapters.

---

## User Experience

### RULE-UX-001: Expired Session Logout Check
All user actions performed on an expired session must redirect the user to the login screen.
- No silent failures or unhandled session expiry states are permitted.

### RULE-UX-002: Slowness Feedback
If any pause or delay is introduced in the code, the user must not be left waiting more than one second without feedback.
- A loading spinner or equivalent indicator must appear within one second of a slow operation starting.

### RULE-UX-003: Error Feedback and Logging
If any error is thrown in C# code:
- The user must be informed that an error has occurred.
- Sufficient error details must be logged to allow diagnosis.
- Swallowing exceptions silently is not permitted.

### RULE-UX-004: Responsive Layout
UI changes must be tested at multiple viewport sizes.
- Test by resizing from large to small and small to large.
- Test starting fresh at different viewport sizes to ensure a consistent responsive layout.

---

## Database

### RULE-DB-001: Table Column Additions
When adding or updating a column in a database table:
- A migration script file must be added (e.g. `AddTaskHubFiltersCreationDate.txt`) with the appropriate `ALTER TABLE` statement.
- The same column must also be added to the original table definition file (e.g. `TaskHubFilters.txt`).
- The `LastUpdatedVersion` field in the original table file must be updated to match the `ToVersion` of the migration script.

Example migration script (`AddTaskHubFiltersCreationDate.txt`):
```sql
ALTER TABLE dbo.TaskHubFilters
    ADD CreationDate DATETIME NULL;
```
The original `TaskHubFilters.txt` must also include the `CreationDate` column definition and have its `LastUpdatedVersion` set to the `ToVersion` of the above script.

---

## Testing

### RULE-TEST-001: Unit Testability of Non-UI Code
All C# code that is not UI or direct-dependency code must be written in a unit-testable manner.
- Classes must not instantiate their own dependencies (use constructor injection).
- Static method calls on external dependencies are not permitted without an abstraction.

### RULE-TEST-002: Acceptance Criteria Coverage
C# implementations of acceptance criteria or test cases must have corresponding unit or integration tests.
- Untested acceptance criteria implementations are not permitted.

### RULE-TEST-003: SOLID Principles
SOLID principles must be considered and reasonably applied:
- **S**: Single Responsibility – each class has one reason to change.
- **O**: Open/Closed – classes open for extension, closed for modification.
- **L**: Liskov Substitution – derived types must be substitutable for their base types.
- **I**: Interface Segregation – clients must not depend on interfaces they do not use.
- **D**: Dependency Inversion – depend on abstractions, not concretions.
