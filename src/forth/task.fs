\ Copyright (c) 2020 Travis Bemann
\
\ This program is free software: you can redistribute it and/or modify
\ it under the terms of the GNU General Public License as published by
\ the Free Software Foundation, either version 3 of the License, or
\ (at your option) any later version.
\
\ This program is distributed in the hope that it will be useful,
\ but WITHOUT ANY WARRANTY; without even the implied warranty of
\ MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
\ GNU General Public License for more details.
\
\ You should have received a copy of the GNU General Public License
\ along with this program.  If not, see <http://www.gnu.org/licenses/>.

\ Compile this to flash
compile-to-flash

\ Declare a RAM variable for the end of free RAM memory
ram-variable free-end

\ Main task
ram-variable main-task

\ Current task
ram-variable current-task

\ Pause count
ram-variable pause-count

\ Debug mode
ram-variable debug-mode

\ The task structure
begin-structure task
  \ Return stack size
  hfield: task-rstack-size

  \ Data stack size
  hfield: task-stack-size

  \ Dictionary size
  field: task-dict-size

  \ Current return stack offset
  hfield: task-rstack-offset

  \ Current data stack offset
  hfield: task-stack-offset

  \ Current dictionary offset
  field: task-dict-offset

  \ Task active state ( > 0 active, <= 0 inactive )
  field: task-active

  \ Next task
  field: task-next
end-structure

\ Get task stack base
: task-stack-base ( task -- addr )
  dup task + swap task-stack-size h@ +
;

\ Get task stack end
: task-stack-end ( task -- addr )
  task +
;

\ Get task return stack base
: task-rstack-base ( task -- addr )
  dup task + over task-stack-size h@ + swap task-rstack-size h@ +
;

\ Get task return stack end
: task-rstack-end ( task -- addr ) task-stack-base ;

\ Get task dictionary base
: task-dict-base ( task -- addr ) dup task-dict-size @ - ;

\ Get task dictionary end
: task-dict-end ( task -- addr ) ;

\ Get task current return stack address
: task-rstack-current ( task -- addr )
  dup task-rstack-base swap task-rstack-offset h@ -
;

\ Get task current stack address
: task-stack-current ( task -- addr )
  dup task-stack-base swap task-stack-offset h@ -
;

\ Get task current dictionary address
: task-dict-current ( task -- addr )
  dup task-dict-base swap task-dict-offset @ +
;

\ Set task current return stack address
: task-rstack-current! ( addr task -- )
  dup task-rstack-base rot - swap task-rstack-offset h!
;

\ Set task current stack address
: task-stack-current! ( addr task -- )
  dup task-stack-base rot - swap task-stack-offset h!
;

\ Set task current dictionary address
: task-dict-current! ( addr task -- )
  dup task-dict-base rot swap - swap task-dict-offset !
;

\ Push data onto a task's stack
: push-task-stack ( x task -- )
  dup task-stack-current cell - tuck swap task-stack-current! !
;

\ Push data onto a task's return stack
: push-task-rstack ( x task -- )
  dup task-rstack-current cell - tuck swap task-rstack-current! !
;

\ Enable a task
: enable-task ( task -- ) dup task-active @ 1 + swap task-active ! ;

\ Disable a task
: disable-task ( task -- ) dup task-active @ 1 - swap task-active ! ;

\ Force-enable a task
: force-enable-task ( task -- )
  dup task-active @
  dup 1 < if drop 1 then
  swap task-active !
;

\ Force-disable a task
: force-disable-task ( task -- )
  dup task-active @
  dup -1 > if drop -1 then
  swap task-active !
;

\ Initialize the main task
: init-main-task ( -- )
  free-end @ task -
  rstack-base @ rstack-end @ - over task-rstack-size h!
  stack-base @ stack-end @ - over task-stack-size h!
  free-end @ task - next-ram-space - over task-dict-size !
  rstack-base @ rp@ - over task-rstack-offset h!
  stack-base @ sp@ - over task-stack-offset h!
  here next-ram-space - over task-dict-offset !
  1 over task-active !
  dup dup task-next !
  dup main-task !
  current-task !
  free-end @ task - free-end !
;

\ Task entry point
: task-entry ( -- )
  ." ***" safe-type
  r> drop
  try
  ?dup if execute then
  current-task @ force-disable-task
  pause
;

\ Dump information on a task
: dump-task-info ( task -- )
  cr ." task-rstack-size:    " dup task-rstack-size h@ .
  cr ." task-stack-size:     " dup task-stack-size h@ .
  cr ." task-dict-size:      " dup task-dict-size @ .
  cr ." task-rstack-offset:  " dup task-rstack-offset h@ .
  cr ." task-stack-offset:   " dup task-stack-offset h@ .
  cr ." task-dict-offset:    " dup task-dict-offset @ .
  cr ." task-rstack-end:     " dup task-rstack-end .
  cr ." task-stack-end:      " dup task-stack-end .
  cr ." task-dict-end:       " dup task-dict-end .
  cr ." task-rstack-base:    " dup task-rstack-base .
  cr ." task-stack-base:     " dup task-stack-base .
  cr ." task-dict-base:      " dup task-dict-base .
  cr ." task-rstack-current: " dup task-rstack-current .
  cr ." task-stack-current:  " dup task-stack-current .
  cr ." task-dict-current:   " task-dict-current .
;
  
\ Spawn a non-main task
: spawn ( xt dict-size stack-size rstack-size -- task )
  2dup + task +
  free-end @ swap -
  tuck task-rstack-size h!
  tuck task-stack-size h!
  tuck task-dict-size !
  0 over task-active !
  current-task @ task-next @ over task-next !
  dup current-task @ task-next !
  dup task-dict-size @ over - free-end !
  0 over task-rstack-offset h!
  0 over task-stack-offset h!
  0 over task-dict-offset !
  ['] task-entry over push-task-rstack
  tuck push-task-stack
\  dup dump-task-info
;

\ Handle PAUSE
: do-pause ( -- )
  pause-count @ 1 + pause-count !
  current-task @
  rp@ over task-rstack-current!
  >r sp@ r> tuck task-stack-current!
  here over task-dict-current!
  task-next @
  begin
    dup task-active @ 1 <
  while
    task-next @
  repeat
  debug-mode @ not if
    dup current-task !
    dup task-stack-base stack-base !
    dup task-stack-end stack-end !
    dup task-rstack-base rstack-base !
    dup task-rstack-end rstack-end !
    dup task-rstack-current rp!
    dup task-dict-current here!
    task-stack-current sp!
  else
    s" a" safe-type
    dup current-task !
    s" b" safe-type
    dup task-stack-base stack-base !
    s" c" safe-type
    dup task-stack-end stack-end !
    s" d" safe-type
    dup task-rstack-base rstack-base !
    s" e" safe-type
    dup task-rstack-end rstack-end !
    s" f" safe-type
    dup task-rstack-current rp!
    s" g" safe-type
    dup task-dict-current here!
    s" h" safe-type
    task-stack-current sp!
    s" i" safe-type

    0 pause-enabled !
    hex
    space
    ." (sp:"
    sp@ .
    space
    ." sp@:"
    sp@ @ .
    space
    ." sp@@:"
    sp@ @ h@ .
    space
    ." rp:"
    rp@ .
    space
    ." rp@:"
    rp@ @ .
    space
    ." rp@@:"
    rp@ @ h@ .
    ." )"
    decimal
    1 pause-enabled !
  then
;
  
\ Initialize RAM variables
: init ( -- )
  init
  stack-end @ free-end !
  init-main-task
  false debug-mode !
  0 pause-count !
  ['] do-pause pause-hook !
  1 pause-enabled !
;

\ Set a cornerstone for multitasking
cornerstone <task>

\ Setting compilation back to RAM
\ compile-to-ram

\ Reboot to initialize multitasking
reboot