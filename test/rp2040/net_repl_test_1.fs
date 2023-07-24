\ Copyright (c) 2023 Travis Bemann
\
\ Permission is hereby granted, free of charge, to any person obtaining a copy
\ of this software and associated documentation files (the "Software"), to deal
\ in the Software without restriction, including without limitation the rights
\ to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
\ copies of the Software, and to permit persons to whom the Software is
\ furnished to do so, subject to the following conditions:
\ 
\ The above copyright notice and this permission notice shall be included in
\ all copies or substantial portions of the Software.
\ 
\ THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
\ IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
\ FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
\ AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
\ LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
\ OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
\ SOFTWARE

begin-module wifi-server-test

  oo import
  cyw43-events import
  cyw43-control import
  cyw43-structs import
  net-misc import
  frame-process import
  net import
  endpoint-process import
  sema import
  lock import
  core-lock import
  
  23 constant pwr-pin
  24 constant dio-pin
  25 constant cs-pin
  29 constant clk-pin
  
  0 constant pio-addr
  0 constant sm-index
  pio::PIO0 constant pio-instance
  
  <cyw43-control> class-size buffer: my-cyw43-control
  <interface> class-size buffer: my-interface
  <frame-process> class-size buffer: my-frame-process
  <arp-handler> class-size buffer: my-arp-handler
  <ip-handler> class-size buffer: my-ip-handler
  <endpoint-process> class-size buffer: my-endpoint-process

  \ Port to set server at
  6667 constant server-port
  
  \ Server lock
  lock-size buffer: server-lock
  
  \ Server core lock
  core-lock-size buffer: server-core-lock
  
  \ Server active
  variable server-active?

  \ Server tx and rx task
  variable server-task

  \ Tx and rx delay
  50 constant server-delay
  
  \ RAM variable for rx buffer read-index
  variable rx-read-index
  
  \ RAM variable for rx buffer write-index
  variable rx-write-index
  
  \ Constant for number of bytes to buffer
  2048 constant rx-buffer-size
  
  \ Rx buffer index mask
  $7FF constant rx-index-mask
  
  \ Rx buffer
  rx-buffer-size buffer: rx-buffer
  
  \ Tx buffer write indices
  2variable tx-write-index
  
  \ Constant for number of bytes to buffer
  2048 constant tx-buffer-size
  
  \ Tx buffer
  tx-buffer-size 2 * buffer: tx-buffers
  
  \ Current tx buffer
  variable current-tx-buffer
  
  \ Tx timeout
  500 constant tx-timeout
  
  \ Tx timeout start
  variable tx-timeout-start
  
  \ Tx semaphore
  sema-size aligned-buffer: tx-sema
  
  \ Tx block semaphore
  sema-size aligned-buffer: tx-block-sema
    
  \ The TCP endpoint
  variable my-endpoint

  \ Get whether the rx buffer is full
  : rx-full? ( -- f )
    rx-write-index @ rx-read-index @
    rx-buffer-size 1- + rx-index-mask and =
  ;

  \ Get whether the rx buffer is empty
  : rx-empty? ( -- f )
    rx-read-index @ rx-write-index @ =
  ;

  \ Write a byte to the rx buffer
  : write-rx ( c -- )
    rx-full? not if
      rx-write-index @ rx-buffer + c!
      rx-write-index @ 1+ rx-index-mask and rx-write-index !
    else
      drop
    then
  ;

  \ Read a byte from the rx buffer
  : read-rx ( -- c )
    rx-empty? not if
      rx-read-index @ rx-buffer + c@
      rx-read-index @ 1+ rx-index-mask and rx-read-index !
    else
      0
    then
  ;

  \ Get whether the tx buffer is full
  : tx-full? ( -- f )
    tx-write-index current-tx-buffer @ cells + @ tx-buffer-size >=
  ;

  \ Write a byte to the tx buffer
  : write-tx ( c -- )
    current-tx-buffer @ { index }
    tx-write-index index cells + { offset-var }
    tx-buffers index tx-buffer-size * + offset-var @ + c!
    1 offset-var +!
  ;
  
  \ Do server transmission and receiving
  : do-server ( -- )
    begin
      tx-timeout systick::systick-counter tx-timeout-start @ - - 0 max { my-timeout }
      my-timeout task::timeout !
      tx-sema ['] take try
      dup ['] task::x-timed-out = if 2drop 0 then
      task::no-timeout task::timeout !
      ?raise
      server-active? @ 0<> my-endpoint @ 0<> and if
        current-tx-buffer @ dup { old-tx-buffer } 1+ 2 umod current-tx-buffer !
        tx-buffers old-tx-buffer tx-buffer-size * +
        tx-write-index old-tx-buffer cells + @
        my-endpoint @ my-interface send-tcp-endpoint
        0 tx-write-index old-tx-buffer cells + !
      then
      systick::systick-counter tx-timeout-start !
      tx-block-sema give
    again
  ;
  
  \ Actually handle received data
  : do-rx-data ( c-addr bytes -- )
    [: { c-addr bytes }
      c-addr bytes + c-addr ?do
        rx-full? not if
          i c@ write-rx
        else
          leave
        then
      loop
    ;] server-lock with-lock
  ;
  
  \ EMIT for telnet
  : telnet-emit ( c -- )
    server-active? @ if
      begin
        [:
          [:
            tx-full? not if
              write-tx
              true
            else
              false
            then
          ;] critical
        ;] server-core-lock with-core-lock
        dup not if tx-sema give tx-block-sema take then
      until
    else
      drop
    then
  ;

  \ EMIT? for telnet
  : telnet-emit? ( -- emit? )
    server-active? @ if
      [: [: tx-full? not ;] critical ;] server-core-lock with-core-lock
    else
      false
    then
  ;

  \ KEY for telnet
  : telnet-key ( -- c )
    begin
      server-active? @ if
        [:
          rx-empty? not if
            read-rx
            true
          else
            false
          then
        ;] server-lock with-lock
        dup not if server-delay ms then
      else
        false
      then
    until
  ;

  \ KEY? for telnet
  : telnet-key? ( -- key? )
    server-active? @ if
      [: rx-empty? not ;] server-lock with-lock
    else
      false
    then
  ;
  
  \ Type data over telnet
  : telnet-type { c-addr bytes -- }
    c-addr bytes + c-addr ?do i c@ telnet-emit loop
  ;
  
  <endpoint-handler> begin-class <tcp-session-handler>
  end-class
  
  <tcp-session-handler> begin-implement
  
    \ Handle a endpoint packet
    :noname { endpoint self -- }
      endpoint endpoint-tcp-state@ TCP_ESTABLISHED = if
        true server-active? !
        endpoint endpoint-rx-data@ do-rx-data
      then
      endpoint my-interface endpoint-done
      endpoint endpoint-tcp-state@ TCP_CLOSE_WAIT = if
        false server-active? !
        endpoint my-interface close-tcp-endpoint
        server-port my-interface allocate-tcp-listen-endpoint if
          my-endpoint !
        else
          drop
        then
      then
    ; define handle-endpoint
  
  end-implement
  
  <tcp-session-handler> class-size buffer: my-tcp-session-handler

  \ Initialize the test
  : init-test ( -- )
    server-lock init-lock
    server-core-lock init-core-lock
    0 current-tx-buffer !
    0 tx-write-index 0 cells + !
    0 tx-write-index 1 cells + !
    0 rx-read-index !
    0 rx-write-index !
    systick::systick-counter tx-timeout-start !
    false server-active? !
    0 my-endpoint !
    no-sema-limit 0 tx-sema init-sema
    no-sema-limit 0 tx-block-sema init-sema
    cyw43-clm::data cyw43-clm::size cyw43-fw::data cyw43-fw::size
    pwr-pin clk-pin dio-pin cs-pin pio-addr sm-index pio-instance
    <cyw43-control> my-cyw43-control init-object
    my-cyw43-control init-cyw43
    my-cyw43-control cyw43-frame-interface@ <interface> my-interface init-object
    192 168 1 44 make-ipv4-addr my-interface intf-ipv4-addr!
    255 255 255 0 make-ipv4-addr my-interface intf-ipv4-netmask!
    my-cyw43-control cyw43-frame-interface@ <frame-process> my-frame-process init-object
    my-interface <arp-handler> my-arp-handler init-object
    my-interface <ip-handler> my-ip-handler init-object
    my-arp-handler my-frame-process add-frame-handler
    my-ip-handler my-frame-process add-frame-handler
    my-interface <endpoint-process> my-endpoint-process init-object
    <tcp-session-handler> my-tcp-session-handler init-object
    my-tcp-session-handler my-endpoint-process add-endpoint-handler
    0 ['] do-server 512 128 1024 1 task::spawn-on-core server-task !
    server-task @ task::run
  ;

  \ Event message buffer
  event-message-size aligned-buffer: my-event

  \ Connect to WiFi
  : connect-wifi { D: ssid D: pass -- }
    begin ssid pass my-cyw43-control join-cyw43-wpa2 nip until
    my-cyw43-control disable-all-cyw43-events
    my-cyw43-control clear-cyw43-events
    ssid pass 4 [: { D: ssid D: pass }
      begin
        my-event my-cyw43-control get-cyw43-event
        my-event evt-event-type @ EVENT_DISASSOC =
        my-event evt-event-type @ EVENT_DISASSOC_IND = or if
          [: cr display-red ." *** RECONNECTING TO WIFI... *** " display-normal ;] usb::with-usb-output
          begin ssid pass my-cyw43-control join-cyw43-wpa2 nip until
          [: cr display-red ." *** RECONNECTED TO WIFI *** " display-normal ;] usb::with-usb-output
        then
      again
    ;] 512 128 1024 task::spawn task::run
    EVENT_DISASSOC my-cyw43-control cyw43-control::enable-cyw43-event
    EVENT_DISASSOC_IND my-cyw43-control cyw43-control::enable-cyw43-event
    my-endpoint-process run-endpoint-process
    my-frame-process run-frame-process
  ;

  \ Start the server
  : start-server ( -- )
    server-port my-interface allocate-tcp-listen-endpoint if
      my-endpoint !
    else
      drop
    then
  ;
  
  \ Set up telnet as a console
  : telnet-console ( -- )
    ['] telnet-key key-hook !
    ['] telnet-key? key?-hook !
    ['] telnet-emit emit-hook !
    ['] telnet-emit? emit?-hook !
  ;

end-module