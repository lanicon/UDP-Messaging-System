using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UDP_Networking {
    class FixedQueue<T> : Queue<T> {
        public FixedQueue(int size) : base(size) {
            _size = size;
        }

        public new void Enqueue(T obj) {
            base.Enqueue(obj);
            while (Count > _size) {
                Dequeue();
            }
        }

        private int _size;
    }
}
