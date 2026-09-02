; A TOML file's outline is its tables, and the keys under them. A table node spans its own header
; and every pair that follows it, so nesting falls out of byte containment the way JSON's does.
; The header key is matched by position rather than by field name because the grammar names none.
(table
  . (_) @name) @def.type

(table_array_element
  . (_) @name) @def.type

(pair
  . (_) @name
  . (_) @body) @def.field
