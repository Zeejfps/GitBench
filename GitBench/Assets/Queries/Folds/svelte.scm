; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(element) @fold
(script_element) @fold
(style_element) @fold
; A block's {:else}, {:then} and {:catch} fold on their own, so its own fold stops before the
; first of them.
(if_statement) @fold
(if_statement [(else_if_block) (else_block)] @stop) @fold
(else_if_block) @fold
(else_block) @fold
(each_statement) @fold
(each_statement (else_block) @stop) @fold
(await_statement) @fold
(await_statement [(then_block) (catch_block)] @stop) @fold
(then_block) @fold
(catch_block) @fold
(key_statement) @fold
(snippet_statement) @fold
(comment) @fold
