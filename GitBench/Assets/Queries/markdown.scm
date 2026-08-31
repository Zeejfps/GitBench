; A document's headings, nested the way the sections they open are. The section is the definition
; rather than the heading, so a fold takes the prose under it.
(section
  (atx_heading
    heading_content: (inline) @name) @body) @def.type

(section
  (setext_heading
    heading_content: (paragraph) @name) @body) @def.type
